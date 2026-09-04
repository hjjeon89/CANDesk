using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CANDesk.App;
public partial class App : Application
{
    private IHost? _host;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<CANDesk.Hal.IBitrateTableProvider, CANDesk.Hal.BitrateTableProvider>();
            services.AddSingleton<CANDesk.Hal.ICanDeviceFactory, CANDesk.Hal.Mock.MockCanDeviceFactory>();
            services.AddSingleton<DeviceConnectionService>();
            services.AddSingleton<DeviceConnectionViewModel>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        }).Build();
        await _host.StartAsync();
        var connection = _host.Services.GetRequiredService<DeviceConnectionService>();
        var connectionViewModel = _host.Services.GetRequiredService<DeviceConnectionViewModel>();
        await connection.ConnectMockAsync(connectionViewModel.BuildConfiguration());
        await _host.Services.GetRequiredService<MainViewModel>().AttachCurrentDeviceAsync();
        _host.Services.GetRequiredService<MainWindow>().Show();
    }
    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null) { await _host.StopAsync(); _host.Dispose(); }
        base.OnExit(e);
    }
}
