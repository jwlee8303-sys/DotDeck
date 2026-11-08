namespace DotDeck.Core.Hooks;

/// <summary>Edits a parsed document's hooks without selecting its serialized representation.</summary>
public interface IHookStore
{
    IReadOnlyList<HookHandlerModel> Read();

    void Write(IReadOnlyList<HookHandlerModel> hooks);
}
