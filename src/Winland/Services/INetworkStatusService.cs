using System;

namespace Winland.Services;

public interface INetworkStatusService
{
    bool IsConnected { get; }
    event EventHandler? ConnectivityChanged;
}
