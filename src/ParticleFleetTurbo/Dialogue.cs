using HarmonyLib;

namespace ParticleFleetTurbo
{
    /// <summary>
    /// <c>[Gameplay] SkipDialogue</c>: closes map dialogue boxes the moment they open, the way the box's own Skip button
    /// does (ConversationPanel.Finish), so the game unpauses, queued dialogue follows and map scripts see the same
    /// "finished" they would after a click. Fading one-line messages are left alone, and so is the map editor.
    /// </summary>
    [HarmonyPatch(typeof(ConversationsManager), nameof(ConversationsManager.ShowConversation))]
    internal static class SkipDialoguePatch
    {
        public static bool Enabled;

        private static void Postfix(string name, bool singleMode)
        {
            if (!Enabled || singleMode || GameSpace.editMode)
                return;
            ConversationPanel panel = GameSpace.instance.conversationPanel;
            if (panel == null || panel.currentConversation == null)
                return;
            Plugin.Log.LogInfo("Skipped dialogue " + name);
            panel.Finish();
        }
    }
}
