using System.Collections.Generic;

namespace BetterSpriteEditor.Operations
{
    /// <summary>
    /// Several commands as one history entry (multi-edit, combine, multi-move).
    /// Undo runs in reverse order.
    /// </summary>
    public class CompositeCommand : IEditorCommand
    {
        private readonly List<IEditorCommand> commands;

        public string Label { get; }

        /// <summary>For snapshotting.</summary>
        internal IReadOnlyList<IEditorCommand> Children => commands;

        public CompositeCommand(string label, List<IEditorCommand> commands)
        {
            Label = label;
            this.commands = commands;
        }

        public void Execute()
        {
            foreach (var command in commands)
                command.Execute();
        }

        public void Undo()
        {
            for (int i = commands.Count - 1; i >= 0; i--)
                commands[i].Undo();
        }
    }
}
