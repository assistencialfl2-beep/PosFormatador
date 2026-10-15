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

    // 2. BUSCA E INSTALAÇÃO AUTOMÁTICA DE DRIVERS (POWERSHELL)
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
    $Downloader = $Session.CreateUpdateDownloader()$Downloader.Updates = $UpdatesToDownload$Downloader.Download()
    
    Write-Host 'Instalando drivers...' -ForegroundColor Yellow
    $Installer = $Session.CreateUpdateInstaller()$Installer.Updates = $UpdatesToDownload$Installer.Install()
    
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

    // 4. INSTALAÇÃO COMPLETA DE PROGRAMAS (AUTO-INSTALA O WINGET SE NÃO EXISTIR)
    private async void BtnRunWinget_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Deseja iniciar a instalação dos programas e do Office 2021?\n\nO instalador verificará e configurará o Winget automaticamente caso o Windows seja novo.",
            "Confirmar Instalação", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        BtnRunWinget.IsEnabled = false;
        Log("Iniciando processo de instalação dos programas...");

        string tempPs1 = Path.Combine(Path.GetTempPath(), "instalar_programas.ps1");

        string psScript = @"$host.UI.RawUI.WindowTitle = 'Instalacao de Programas e Office 2021'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Host '======================================================' -ForegroundColor Cyan
Write-Host '       VERIFICANDO DEPENDENCIAS DO SISTEMA            ' -ForegroundColor Cyan
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host ''

# 1. Verifica se o Winget existe; se nao existir, baixa e instala
$wingetCmd = Get-Command winget -ErrorAction SilentlyContinue

if (-not $wingetCmd) {
    Write-Host '[!] Winget nao encontrado. Instalando o Windows Package Manager automaticamente...' -ForegroundColor Yellow
    $tempDir = Join-Path$env:TEMP 'winget_install'
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    
    try {
        Write-Host 'Baixando dependencias (VCLibs e UI.Xaml)...' -ForegroundColor Gray
        Invoke-WebRequest -Uri 'https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx' -OutFile ""$tempDir\vclibs.appx"" -UseBasicParsing
        Invoke-WebRequest -Uri 'https://github.com/microsoft/microsoft-ui-xaml/releases/download/v2.8.6/Microsoft.UI.Xaml.2.8.x64.appx' -OutFile ""$tempDir\uixaml.appx"" -UseBasicParsing
        
        Write-Host 'Baixando Winget oficial...' -ForegroundColor Gray
        Invoke-WebRequest -Uri 'https://github.com/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle' -OutFile ""$tempDir\winget.msixbundle"" -UseBasicParsing
        
        Write-Host 'Registrando pacotes no Windows...' -ForegroundColor Yellow
        Add-AppxPackage -Path ""$tempDir\vclibs.appx"" -ErrorAction SilentlyContinue
        Add-AppxPackage -Path ""$tempDir\uixaml.appx"" -ErrorAction SilentlyContinue
        Add-AppxPackage -Path ""$tempDir\winget.msixbundle""
        
        # Atualiza o PATH da sessao atual
        $env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path','User')
        Write-Host '[OK] Winget configurado com sucesso!' -ForegroundColor Green
    }
    catch {
        Write-Host ('[AVISO] Falha ao instalar pacote msixbundle: ' + $_.Exception.Message) -ForegroundColor Red
    }
} else {
    Write-Host '[OK] Winget ja esta instalado no sistema.' -ForegroundColor Green
}

Write-Host ''
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host '           INSTALANDO PROGRAMAS ESSENCIAIS            ' -ForegroundColor Cyan
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host ''

$apps = @(
    @{ Name = 'Google Chrome'; Id = 'Google.Chrome' },
    @{ Name = '7-Zip'; Id = '7zip.7zip' },
    @{ Name = 'WinRAR'; Id = 'RARLab.WinRAR' },
    @{ Name = 'Adobe Acrobat Reader'; Id = 'Adobe.Acrobat.Reader.64-bit' },
    @{ Name = 'Java JRE 8'; Id = 'Oracle.JavaRuntimeEnvironment' },
    @{ Name = 'Visual C++ All-in-One'; Id = 'abbodi1406.vcredist' },
    @{ Name = 'DirectX'; Id = 'Microsoft.DirectX' }
)

$wArgs = '--silent --accept-source-agreements --accept-package-agreements'

foreach ($app in$apps) {
    Write-Host ('Instalando ' + $app.Name + '...') -ForegroundColor Yellow
    winget install --id $app.Id --silent --accept-source-agreements --accept-package-agreements
}

Write-Host ''
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host '           INSTALANDO MICROSOFT OFFICE 2021           ' -ForegroundColor Cyan
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host ''

$tempOffice = Join-Path$env:TEMP 'Office2021_Setup'
New-Item -ItemType Directory -Path $tempOffice -Force | Out-Null
Set-Location $tempOffice

$xmlConfig = @'
<Configuration>
  <Add OfficeClientEdition=""64"" Channel=""PerpetualVL2021"">
    <Product ID=""ProPlus2021Volume"">
      <Language ID=""pt-br"" />
    </Product>
  </Add>
  <Display Level=""None"" AcceptEULA=""TRUE"" />
</Configuration>
'@

Set-Content -Path ""$tempOffice\configuration.xml"" -Value $xmlConfig

Write-Host 'Baixando ferramenta ODT da Microsoft...' -ForegroundColor Yellow
$wc = New-Object System.Net.WebClient
$wc.Headers.Add('User-Agent', 'Mozilla/5.0')

try {
    $wc.DownloadFile('https://download.microsoft.com/download/2/7/A/27AF1BE6-DD20-4CB4-B154-EBAB8A7D4A7E/officedeploymenttool_17830-20162.exe', ""$tempOffice\odt.exe"")
} catch {
    $wc.DownloadFile('https://download.microsoft.com/download/2/7/A/27AF1E07-7E7F-4BEE-A625-27B5E28456F4/officedeploymenttool_16.0.17628.20144.exe', ""$tempOffice\odt.exe"")
}

if (Test-Path ""$tempOffice\odt.exe"") {
    Write-Host 'Extraindo ferramentas...' -ForegroundColor Gray
    Start-Process -FilePath ""$tempOffice\odt.exe"" -ArgumentList ""/quiet /extract:`""$tempOffice`"""" -Wait
    
    Write-Host 'Instalando Office 2021 em segundo plano (aguarde)...' -ForegroundColor Yellow
    Start-Process -FilePath ""$tempOffice\setup.exe"" -ArgumentList ""/configure configuration.xml"" -Wait
    Write-Host '[OK] Office 2021 instalado!' -ForegroundColor Green
} else {
    Write-Host 'Tentando instalar Office via Winget...' -ForegroundColor Yellow
    winget install --id Microsoft.Office --silent --accept-source-agreements --accept-package-agreements
}

Remove-Item -Path $tempOffice -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host '          TODOS OS PROGRAMAS FORAM INSTALADOS!        ' -ForegroundColor Green
Write-Host '======================================================' -ForegroundColor Cyan
Read-Host 'Pressione ENTER para fechar...'
";

        try
        {
            await File.WriteAllTextAsync(tempPs1, psScript);
            await RunVisibleProcessAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{tempPs1}\"");
            Log("Processo de instalação finalizado!");
        }
        catch (Exception ex)
        {
            Log($"Erro na instalação: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempPs1)) try { File.Delete(tempPs1); } catch { }
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
