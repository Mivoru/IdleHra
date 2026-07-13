using UnityEngine;
using System.Threading.Tasks;

namespace FolkIdle.Client.Network
{
    public class ClientInputProxy : MonoBehaviour
    {
        public WebSocketClient NetworkClient;

        // Called via Unity UI Button Event
        public void OnChangeActivityClicked(long activityId)
        {
            var command = new ClientCommandPacket
            {
                Command = CommandType.ChangeActivity,
                TargetId = activityId
            };
            
            _ = SendCommandSafelyAsync(command);
        }

        // Called via Unity UI Button Event
        public void OnTriggerForgeFusion(long targetEquipId, long secEquipId, long terEquipId)
        {
            var command = new ClientCommandPacket
            {
                Command = CommandType.ExecuteForgeFusion,
                TargetId = targetEquipId,
                SecondaryId = secEquipId,
                TertiaryId = terEquipId
            };
            
            _ = SendCommandSafelyAsync(command);
        }

        private async Task SendCommandSafelyAsync(ClientCommandPacket command)
        {
            if (NetworkClient != null)
            {
                await NetworkClient.SendCommandAsync(command);
            }
        }
    }
}
