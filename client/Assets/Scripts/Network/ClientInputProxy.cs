using UnityEngine;

namespace FolkIdle.Client.Network
{
    public class ClientInputProxy : MonoBehaviour
    {
        public WebSocketClient NetworkClient;

        // Called via Unity UI Button Event
        public void OnChangeActivityClicked(long activityId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendCommandZeroAlloc((byte)CommandType.ChangeActivity, (int)activityId);
            }
        }

        // Called via Unity UI Button Event
        public void OnTriggerForgeFusion(long targetEquipId, long secEquipId, long terEquipId)
        {
            if (NetworkClient != null)
            {
                NetworkClient.SendFusionCommandZeroAlloc(targetEquipId, secEquipId, terEquipId);
            }
        }
    }
}
