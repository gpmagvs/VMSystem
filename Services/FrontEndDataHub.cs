using Microsoft.AspNetCore.SignalR;
using VMSystem.BackgroundServices;

namespace VMSystem.Services
{
    public class FrontEndDataHub : Hub
    {
        ILogger<FrontEndDataHub> logger;
        public FrontEndDataHub(ILogger<FrontEndDataHub> logger)
        {
            this.logger = logger;
        }
        public override async Task OnConnectedAsync()
        {
            logger.LogTrace($"{this.Context.ConnectionId} Connected");
            await Clients.All.SendAsync("ReceiveData", "VMS", FrontEndDataCollectionBackgroundService._previousData);
            await base.OnConnectedAsync();
        }
        public override Task OnDisconnectedAsync(Exception? exception)
        {
            logger.LogTrace($"{this.Context.ConnectionId} Disconnected {exception?.Message}");
            return base.OnDisconnectedAsync(exception);
        }
        public async Task SendData(string user, string message)
        {
            await Clients.All.SendAsync("ReceiveData", message);
        }
    }
}
