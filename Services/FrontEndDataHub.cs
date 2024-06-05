using Microsoft.AspNetCore.SignalR;

namespace VMSystem.Services
{
    public class FrontEndDataHub : Hub
    {
        public FrontEndDataHub()
        {

        }

        public async Task SendData(string user, string message)
        {
            await Clients.All.SendAsync("ReceiveData", message);
        }
    }
}
