using System;

namespace BetterSpriteEditor.Operations
{
    internal static class SessionTransition
    {
        // DisplayDialogComplex indices: Save, Cancel, Discard. Do not discard on a failed save.
        internal static bool TryLeave(bool dirty, Func<int> choose, Func<bool> save, Action discardRecovery)
        {
            if (!dirty)
                return true;
            int choice = choose();
            if (choice == 1 || (choice != 0 && choice != 2))
                return false;
            if (choice == 0 && !save())
                return false;
            discardRecovery();
            return true;
        }
    }
}
