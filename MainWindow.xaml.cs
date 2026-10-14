using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private async void BtnRunWinget_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Deseja instalar os programas essenciais (Chrome, 7-Zip, WinRAR, Adobe Reader, Java, VC++ e DirectX)?",
            "Confirmar Instalação", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        string[] apps = {
            "Google.Chrome",
            "7zip.7zip",
            "RARLab.WinRAR",
            "Adobe.Acrobat.Reader.64-bit",
            "Oracle.JavaRuntimeEnvironment",
            "abbodi1406.vcredist",
            "Microsoft.DirectX"
        };

        BtnRunWinget.IsEnabled = false;

        foreach (var app in apps)
        {
            Log($"Instalando {app} via winget...");
            await RunProcessAsync("winget", $"install --id {app} --silent --accept-source-agreements --accept-package-agreements");
        }

        Log("Instalação de programas finalizada!");
        BtnRunWinget.IsEnabled = true;
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

                proc.OutputDataReceived += (s, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) Dispatcher.Invoke(() => Log(ev.Data)); };
                proc.ErrorDataReceived += (s, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) Dispatcher.Invoke(() => Log($"[AVISO] {ev.Data}")); };

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => Log($"Erro ao rodar {file}: {ex.Message}"));
            }
        });
    }
}
