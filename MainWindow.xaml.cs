using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;

namespace PosFormatador;

public partial class MainWindow : Window
{
    private List<HardwareDevice> _allDevices = new();

    public MainWindow()
    {
        InitializeComponent();
        Log("Aplicativo inicializado. Clique em 'Escanear Hardware'.");
    }

    private void Log(string message)
    {
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        TxtLog.ScrollToEnd();
    }

    // 1. ESCANEAR HARDWARE (WMI)
    private async void BtnScanHardware_Click(object sender, RoutedEventArgs e)
    {
        BtnScanHardware.IsEnabled = false;
        Log("Iniciando varredura WMI de dispositivos Plug and Play...");

        _allDevices = await Task.Run(GetHardwareDevices);
        ApplyFilter();

        int missing = _allDevices.Count(d => d.HasDriverIssue);
        Log($"Concluído! Total: {_allDevices.Count} itens | Faltando driver: {missing}");
        BtnScanHardware.IsEnabled = true;
    }

    private List<HardwareDevice> GetHardwareDevices()
    {
        var list = new List<HardwareDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, HardwareID, ConfigManagerErrorCode, Status FROM Win32_PnPEntity");

            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "Dispositivo Desconhecido";
                string deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;
                uint errorCode = (uint)(obj["ConfigManagerErrorCode"] ?? 0);
                string status = obj["Status"]?.ToString() ?? "Unknown";

                string hwId = string.Empty;
                if (obj["HardwareID"] is string[] ids && ids.Length > 0)
                {
                    hwId = ids[0];
                }

                list.Add(new HardwareDevice
                {
                    Name = name,
                    DeviceId = deviceId,
                    HardwareId = hwId,
                    Status = status,
                    ErrorCode = errorCode
                });
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => Log($"Erro WMI: {ex.Message}"));
        }
        return list;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        GridDevices.ItemsSource = ChkOnlyIssues.IsChecked == true
            ? _allDevices.Where(d => d.HasDriverIssue).ToList()
            : _allDevices.ToList();
    }

    // 2. BUSCA E INSTALAÇÃO AUTOMÁTICA DE DRIVERS (POWERSHELL DIRETO)
    private async void BtnAutoDrivers_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Deseja buscar, baixar e instalar os drivers ausentes pelo catálogo oficial do Windows?\n\nUma janela do PowerShell será aberta com o progresso.",
            "Download de Drivers", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        BtnAutoDrivers.IsEnabled = false;
        Log("Iniciando busca automática de drivers via Windows Update Catalog...");

        string tempPs1 = Path.Combine(Path.GetTempPath(), "atualizar_drivers.ps1");

        string psContent = @"$host.UI.RawUI.WindowTitle = 'Atualizacao Automatica de Drivers'
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host '       ANALISANDO E BAIXANDO DRIVERS DO SISTEMA       ' -ForegroundColor Cyan
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host ''

$Session = New-Object -ComObject Microsoft.Update.Session
$Searcher = $Session.CreateUpdateSearcher()
$Searcher.ServerSelection = 2

Write-Host 'Buscando drivers compativeis pendentes...' -ForegroundColor Yellow
$Results = $Searcher.Search(""Type='Driver' and IsInstalled=0"")

if ($Results.Updates.Count -eq 0) {
    Write-Host 'Nenhum driver pendente foi encontrado. Todos estao atualizados!' -ForegroundColor Green
} else {
    Write-Host ('Foram encontrados ' + $Results.Updates.Count + ' driver(s) para atualizar:') -ForegroundColor Cyan
    $UpdatesToDownload = New-Object -ComObject Microsoft.Update.UpdateColl
    foreach ($u in $Results.Updates) {
        $UpdatesToDownload.Add($u) | Out-Null
        Write-Host (' - ' + $u.Title)
    }
    
    Write-Host '`nBaixando drivers...' -ForegroundColor Yellow
    $Downloader = $Session.CreateUpdateDownloader()
    $Downloader.Updates = $UpdatesToDownload
    $Downloader.Download()
    
    Write-Host 'Instalando drivers...' -ForegroundColor Yellow
    $Installer = $Session.CreateUpdateInstaller()
    $Installer.Updates = $UpdatesToDownload
    $Installer.Install()
    
    Write-Host 'Drivers instalados com sucesso!' -ForegroundColor Green
}

Write-Host ''
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host '                 PROCESSO CONCLUIDO!                  ' -ForegroundColor Cyan
Write-Host '======================================================' -ForegroundColor Cyan
Read-Host 'Pressione ENTER para fechar esta janela...'
";

        try
        {
            await File.WriteAllTextAsync(tempPs1, psContent);
            await RunVisibleProcessAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{tempPs1}\"");
            Log("Processo de atualização de drivers concluído. Reescaneie o hardware para conferir.");
        }
        catch (Exception ex)
        {
            Log($"Erro ao atualizar drivers: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempPs1)) try { File.Delete(tempPs1); } catch { }
            BtnAutoDrivers.IsEnabled = true;
        }
    }

    // 3. INSTALAÇÃO MANUAL DE DRIVER LOCAL (.INF)
    private async void BtnInstallInf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Arquivos de Driver (*.inf)|*.inf",
            Title = "Selecione o arquivo .INF do Driver"
        };

        if (dialog.ShowDialog() == true)
        {
            Log($"Instalando driver local: {dialog.FileName}");
            await RunVisibleProcessAsync("pnputil.exe", $"/add-driver \"{dialog.FileName}\" /install");
            Log("Comando pnputil concluído.");
        }
    }

    // 4. INSTALAÇÃO COMPLETA DE PROGRAMAS + OFFICE 2021
    private async void BtnRunWinget_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Deseja iniciar a instalação dos programas e do Office 2021?\n\nUma janela do Prompt será exibida com o progresso de cada item.",
            "Confirmar Instalação", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        BtnRunWinget.IsEnabled = false;
        Log("Abrindo instalador de programas em janela de console...");

        string tempBat = Path.Combine(Path.GetTempPath(), "instalar_programas_office.bat");

        string scriptContent = @"@echo off
title Instalacao de Programas e Office 2021
color 0A

set ""WINGET_CMD=winget""
where winget >nul 2>&1
if %errorLevel% neq 0 (
    for /d %%i in (""%LOCALAPPDATA%\Microsoft\WindowsApps\Microsoft.DesktopAppInstaller_*"") do (
        if exist ""%%i\winget.exe"" set ""WINGET_CMD=%%i\winget.exe""
    )
)

echo ======================================================
echo           INSTALANDO PROGRAMAS ESSENCIAIS
echo ======================================================
echo.

set W_ARGS=--silent --accept-source-agreements --accept-package-agreements

echo [1/7] Google Chrome...
%WINGET_CMD% install --id Google.Chrome %W_ARGS%

echo [2/7] 7-Zip...
%WINGET_CMD% install --id 7zip.7zip %W_ARGS%

echo [3/7] WinRAR...
%WINGET_CMD% install --id RARLab.WinRAR %W_ARGS%

echo [4/7] Adobe Acrobat Reader...
%WINGET_CMD% install --id Adobe.Acrobat.Reader.64-bit %W_ARGS%

echo [5/7] Java Runtime (JRE 8)...
%WINGET_CMD% install --id Oracle.JavaRuntimeEnvironment %W_ARGS%

echo [6/7] Visual C++ Runtimes All-in-One...
%WINGET_CMD% install --id abbodi1406.vcredist %W_ARGS%

echo [7/7] DirectX...
%WINGET_CMD% install --id Microsoft.DirectX %W_ARGS%

echo.
echo ======================================================
echo           INSTALANDO MICROSOFT OFFICE 2021
echo ======================================================
echo.

set ""TEMP_OFFICE=%TEMP%\Office2021_Setup""
if not exist ""%TEMP_OFFICE%"" mkdir ""%TEMP_OFFICE%""
cd /d ""%TEMP_OFFICE%""

(
echo ^<Configuration^>
echo   ^<Add OfficeClientEdition=""64"" Channel=""PerpetualVL2021""^>
echo     ^<Product ID=""ProPlus2021Volume""^>
echo       ^<Language ID=""pt-br"" /^>
echo     ^</Product^>
echo   ^</Add^>
echo   ^<Display Level=""None"" AcceptEULA=""TRUE"" /^>
echo ^</Configuration^>
) > configuration.xml

echo Baixando ferramenta de instalacao da Microsoft (ODT)...
powershell -Command ""[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; $wc = New-Object System.Net.WebClient; $wc.Headers.Add('User-Agent', 'Mozilla/5.0'); $wc.DownloadFile('https://download.microsoft.com/download/2/7/A/27AF1BE6-DD20-4CB4-B154-EBAB8A7D4A7E/officedeploymenttool_17830-20162.exe', 'odt.exe')""

if not exist ""odt.exe"" (
    echo Link 1 falhou. Tentando link alternativo...
    powershell -Command ""[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; $wc = New-Object System.Net.WebClient; $wc.Headers.Add('User-Agent', 'Mozilla/5.0'); $wc.DownloadFile('https://download.microsoft.com/download/2/7/A/27AF1E07-7E7F-4BEE-A625-27B5E28456F4/officedeploymenttool_16.0.17628.20144.exe', 'odt.exe')""
)

if not exist ""odt.exe"" (
    echo [AVISO] Nao foi possivel baixar o ODT pelos links diretos. Tentando via Winget...
    %WINGET_CMD% install --id Microsoft.Office %W_ARGS%
    goto office_fim
)

echo Extraindo ferramentas do Office...
odt.exe /quiet /extract:""%TEMP_OFFICE%""

echo Baixando e instalando componentes do Office 2021 (Aguarde alguns minutos)...
setup.exe /configure configuration.xml

:office_fim
cd /d ""%TEMP%""
rmdir /s /q ""%TEMP_OFFICE%""

echo.
echo ======================================================
echo          TODOS OS PROGRAMAS FORAM INSTALADOS!
echo ======================================================
echo Pressione qualquer tecla para fechar esta janela...
pause >nul
";

        try
        {
            await File.WriteAllTextAsync(tempBat, scriptContent);
            await RunVisibleProcessAsync(tempBat);
            Log("Instalação de programas e Office concluída!");
        }
        catch (Exception ex)
        {
            Log($"Erro na instalação: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempBat)) try { File.Delete(tempBat); } catch { }
            BtnRunWinget.IsEnabled = true;
        }
    }

    private Task RunVisibleProcessAsync(string filePath, string args = "")
    {
        return Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = filePath,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas"
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        });
    }
}
