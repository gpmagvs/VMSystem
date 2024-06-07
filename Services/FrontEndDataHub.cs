using Microsoft.AspNetCore.SignalR;
using VMSystem.BackgroundServices;

namespace VMSystem.Services
{
    public class FrontEndDataHub : Hub
    {
        public FrontEndDataHub()
        {

        }
        public override async Task OnConnectedAsync()
        {
            await Clients.All.SendAsync("ReceiveData", "VMS", FrontEndDataCollectionBackgroundService._previousData);
            await base.OnConnectedAsync();
        }
        public async Task SendData(string user, string message)
        {
            await Clients.All.SendAsync("ReceiveData", message);
        }
    }
}
