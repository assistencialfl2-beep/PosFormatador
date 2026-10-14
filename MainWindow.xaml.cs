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
        Log("Aplicativo pronto. Clique em 'Escanear Hardware'.");
    }

    private void Log(string message)
    {
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        TxtLog.ScrollToEnd();
    }

    // 1. VARREDURA DE HARDWARE WMI
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
            Dispatcher.Invoke(() => Log($"Erro na leitura WMI: {ex.Message}"));
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

    // 2. INSTALAÇÃO MANUAL DE DRIVER .INF
    private async void BtnInstallInf_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Arquivos de Driver (*.inf)|*.inf",
            Title = "Selecione o arquivo .INF do Driver"
        };

        if (dialog.ShowDialog() == true)
        {
            Log($"Iniciando instalacao do driver: {dialog.FileName}");
            await RunProcessAsync("pnputil.exe", $"/add-driver \"{dialog.FileName}\" /install");
            Log("Processo do pnputil concluído.");
        }
    }

    // 3. INSTALAÇÃO COMPLETA DE PROGRAMAS + OFFICE 2021
    private async void BtnRunWinget_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Deseja iniciar a instalação completa dos programas (Chrome, 7-Zip, WinRAR, Adobe Reader, Java, VC++, DirectX e Office 2021)?\n\nIsso pode levar alguns minutos.",
            "Confirmar Instalação", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        BtnRunWinget.IsEnabled = false;
        Log("Iniciando script completo de instalação de programas e Office...");

        // Cria o script .bat temporário com todo o seu código
        string tempBatPath = Path.Combine(Path.GetTempPath(), "install_apps_office.bat");

        string scriptContent = @"@echo off
set WINGET_ARGS=--silent --accept-source-agreements --accept-package-agreements

echo [1/8] Instalando Google Chrome...
winget install --id Google.Chrome %WINGET_ARGS%

echo [2/8] Instalando 7-Zip...
winget install --id 7zip.7zip %WINGET_ARGS%

echo [3/8] Instalando WinRAR...
winget install --id RARLab.WinRAR %WINGET_ARGS%

echo [4/8] Instalando Adobe Acrobat Reader...
winget install --id Adobe.Acrobat.Reader.64-bit %WINGET_ARGS%

echo [5/8] Instalando Java...
winget install --id Oracle.JavaRuntimeEnvironment %WINGET_ARGS%

echo [6/8] Instalando Visual C++ Runtimes All-in-One...
winget install --id abbodi1406.vcredist %WINGET_ARGS%

echo [7/8] Instalando DirectX...
winget install --id Microsoft.DirectX %WINGET_ARGS%

echo [8/8] Baixando e Instalando Office 2021 (pode levar alguns minutos)...
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

powershell -Command ""[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://download.microsoft.com/download/2/7/A/27AF1E07-7E7F-4BEE-A625-27B5E28456F4/officedeploymenttool_16.0.17628.20144.exe' -OutFile 'odt.exe'""
odt.exe /quiet /extract:""%TEMP_OFFICE%""
setup.exe /configure configuration.xml

cd /d ""%TEMP%""
rmdir /s /q ""%TEMP_OFFICE%""

echo.
echo ======================================================
echo           TODOS OS PROGRAMAS FORAM INSTALADOS!
echo ======================================================
";

        try
        {
            await File.WriteAllTextAsync(tempBatPath, scriptContent);
            await RunProcessAsync("cmd.exe", $"/c \"{tempBatPath}\"");
        }
        finally
        {
            if (File.Exists(tempBatPath))
            {
                try { File.Delete(tempBatPath); } catch { }
            }
            BtnRunWinget.IsEnabled = true;
            Log("Processo de instalação finalizado!");
        }
    }

    private Task RunProcessAsync(string file, string args)
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return;

                proc.OutputDataReceived += (s, ev) =>
                {
                    if (!string.IsNullOrWhiteSpace(ev.Data))
                        Dispatcher.Invoke(() => Log(ev.Data));
                };
                proc.ErrorDataReceived += (s, ev) =>
                {
                    if (!string.IsNullOrWhiteSpace(ev.Data))
                        Dispatcher.Invoke(() => Log($"[AVISO] {ev.Data}"));
                };

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => Log($"Erro ao executar processo: {ex.Message}"));
            }
        });
    }
}
