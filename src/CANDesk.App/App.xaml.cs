using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CANDesk.App;
public partial class App : Application
{
    private IHost? _host;
    private MainViewModel? _mainViewModel;
    private DeviceConnectionService? _connection;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<CANDesk.Hal.IBitrateTableProvider, CANDesk.Hal.BitrateTableProvider>();
            services.AddSingleton<CANDesk.Hal.IBitTimingCalculator, CANDesk.Hal.BitTimingCalculator>();
            services.AddSingleton<CANDesk.Hal.ICanDeviceFactory, CANDesk.Hal.Peak.PeakCanDeviceFactory>();
            services.AddSingleton<CANDesk.Hal.ICanDeviceFactory, CANDesk.Hal.Vector.VectorCanDeviceFactory>();
            services.AddSingleton<DeviceConnectionService>();
            services.AddSingleton<DeviceConnectionViewModel>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        }).Build();
        await _host.StartAsync();
        _connection = _host.Services.GetRequiredService<DeviceConnectionService>();
        // No auto-connect at startup: the app opens disconnected and the user picks a real vendor
        // and channel, then hits Connect (MainViewModel.Connect already calls AttachCurrentDeviceAsync
        // once that succeeds).
        _mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            ShutdownAsync().GetAwaiter().GetResult();
        }
        finally
        {
            base.OnExit(e);
        }
    }

    private async Task ShutdownAsync()
    {
        if (_mainViewModel is not null)
        {
            await _mainViewModel.DisposeAsync().ConfigureAwait(false);
            _mainViewModel = null;
        }

        if (_connection is not null)
        {
            await _connection.DisconnectAsync().ConfigureAwait(false);
            _connection = null;
        }

        if (_host is not null)
        {
            await _host.StopAsync().ConfigureAwait(false);
            _host.Dispose();
            _host = null;
        }
    }
}
