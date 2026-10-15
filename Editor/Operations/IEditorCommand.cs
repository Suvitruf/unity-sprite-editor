namespace BetterSpriteEditor.Operations
{
    /// <summary>
    /// One undoable action on a SpriteDocument. Commands capture everything they need
    /// up front, so Execute/Undo must stay valid no matter how far the history rewinds.
    /// </summary>
    public interface IEditorCommand
    {
        /// <summary>Human-readable action name, shown in the history panel and undo tooltips.</summary>
        string Label { get; }

        void Execute();

        void Undo();
    }
}
