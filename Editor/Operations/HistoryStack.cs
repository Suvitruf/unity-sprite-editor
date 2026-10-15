using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;

namespace BetterSpriteEditor.Operations
{
    /// <summary>
    /// Custom undo/redo history for one document (deliberately independent from UnityEditor.Undo:
    /// we edit an in-memory document and need a browsable action list). Owns the document's dirty
    /// state via save-point tracking, so "undo back to the last Apply" reads as not modified.
    /// </summary>
    public class HistoryStack
    {
        private readonly SpriteDocument document;
        private readonly List<IEditorCommand> undoStack = new();
        private readonly List<IEditorCommand> redoStack = new();

        // Undo-stack depth at the last save; -1 when the save point was lost to a discarded redo branch.
        private int savePointDepth;

        /// <summary>Raised after every do/undo/redo/save-point change.</summary>
        public event Action Changed;

        public bool CanUndo => undoStack.Count > 0;
        public bool CanRedo => redoStack.Count > 0;
        public string UndoLabel => CanUndo ? undoStack[^1].Label : string.Empty;
        public string RedoLabel => CanRedo ? redoStack[^1].Label : string.Empty;

        /// <summary>Undo history, oldest first — for the history panel.</summary>
        public IReadOnlyList<IEditorCommand> UndoEntries => undoStack;

        /// <summary>Redoable entries in stack order — the LAST element is the next redo.</summary>
        public IReadOnlyList<IEditorCommand> RedoEntries => redoStack;

        /// <summary>Current position: number of applied entries (0 = initial document state).</summary>
        public int Position => undoStack.Count;

        /// <summary>Undo depth at the last save; -1 when unreachable. For snapshotting.</summary>
        public int SavePointDepth => savePointDepth;

        /// <summary>
        /// Replaces the stacks wholesale (recovery restore). The commands must already reflect
        /// the document's current state — nothing is executed here, only recorded.
        /// </summary>
        public void RestoreState(
            List<IEditorCommand> undoEntries, List<IEditorCommand> redoEntries, int savePoint)
        {
            undoStack.Clear();
            undoStack.AddRange(undoEntries);
            redoStack.Clear();
            redoStack.AddRange(redoEntries);
            savePointDepth = savePoint;
            AfterChange();
        }

        /// <summary>
        /// Photoshop-style jump: undoes or redoes until the given position is reached.
        /// Position 0 is the freshly opened document; UndoEntries.Count + RedoEntries.Count is the tip.
        /// </summary>
        public void JumpTo(int position)
        {
            position = Math.Clamp(position, 0, undoStack.Count + redoStack.Count);
            while (Position > position && CanUndo)
                Undo();
            while (Position < position && CanRedo)
                Redo();
        }

        public HistoryStack(SpriteDocument document)
        {
            this.document = document;
        }

        /// <summary>Executes the command and records it.</summary>
        public void Do(IEditorCommand command)
        {
            command.Execute();
            Push(command);
        }

        /// <summary>
        /// Records a command whose change is already applied (e.g. a finished drag where the
        /// document was mutated live). Execute() is not called now but must redo the change.
        /// </summary>
        public void PushApplied(IEditorCommand command)
        {
            Push(command);
        }

        public void Undo()
        {
            if (!CanUndo)
                return;

            var command = undoStack[^1];
            undoStack.RemoveAt(undoStack.Count - 1);
            command.Undo();
            redoStack.Add(command);
            AfterChange();
        }

        public void Redo()
        {
            if (!CanRedo)
                return;

            var command = redoStack[^1];
            redoStack.RemoveAt(redoStack.Count - 1);
            command.Execute();
            undoStack.Add(command);
            AfterChange();
        }

        /// <summary>Call right after the document is saved to the asset.</summary>
        public void MarkSavePoint()
        {
            savePointDepth = undoStack.Count;
            AfterChange();
        }

        private void Push(IEditorCommand command)
        {
            // A save point inside the redo branch we are about to discard is gone forever.
            if (savePointDepth > undoStack.Count)
                savePointDepth = -1;

            undoStack.Add(command);
            redoStack.Clear();
            AfterChange();
        }

        private void AfterChange()
        {
            if (undoStack.Count == savePointDepth)
                document.ClearDirty();
            else
                document.MarkDirty();
            Changed?.Invoke();
        }
    }
}
