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

    // 2. BUSCA E INSTALAÇÃO AUTOMÁTICA DE DRIVERS PELA INTERNET
    private async void BtnAutoDrivers_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Deseja buscar, baixar e instalar os drivers ausentes via catálogo oficial do Windows?\n\nUma janela do Prompt será exibida com o progresso.",
            "Download de Drivers", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        BtnAutoDrivers.IsEnabled = false;
        Log("Iniciando busca automática de drivers via Windows Update Catalog...");

        string tempScript = Path.Combine(Path.GetTempPath(), "atualizar_drivers.bat");

        string scriptContent = @"@echo off
title Atualizacao Automatica de Drivers
color 0B
echo ======================================================
echo       ANALISANDO E BAIXANDO DRIVERS DO SISTEMA
echo ======================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    ""$Session = New-Object -ComObject Microsoft.Update.Session;"" ^
    ""$Searcher =$Session.CreateUpdateSearcher();"" ^
    ""$Searcher.ServerSelection = 2;"" ^
    ""Write-Host 'Buscando drivers compativeis pendentes...' -ForegroundColor Yellow;"" ^
    ""$Results =$Searcher.Search('Type=''Driver'' and IsInstalled=0');"" ^
    ""if ($Results.Updates.Count -eq 0) {"" ^
    ""   Write-Host 'Nenhum driver pendente foi encontrado. Todos estao atualizados!' -ForegroundColor Green;"" ^
    ""} else {"" ^
    ""   Write-Host ('Foram encontrados ' + $Results.Updates.Count + ' driver(s) para atualizar
