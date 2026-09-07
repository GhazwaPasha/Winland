using System;
using Windows.Networking.Connectivity;

namespace Winland.Services;

/// <summary>Read-only connectivity glyph — not a settings toggle, so kept real.</summary>
public sealed class NetworkStatusService : INetworkStatusService, IDisposable
{
    public bool IsConnected { get; private set; }

    public event EventHandler? ConnectivityChanged;

    public NetworkStatusService()
    {
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        Refresh();
    }

    private void OnNetworkStatusChanged(object sender) => Refresh();

    private void Refresh()
    {
        try
        {
            IsConnected = NetworkInformation.GetInternetConnectionProfile() is not null;
        }
        catch
        {
            IsConnected = false;
        }

        ConnectivityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
}
