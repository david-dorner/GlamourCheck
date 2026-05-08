using System;
using Dalamud.Plugin.Services;

namespace GlamourCheck.Services;

/// <summary>
/// Tracks the active character key and reloads collection state when the logged-in character changes.
/// </summary>
public sealed class CharacterContextService : IDisposable
{
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly ICollectionRepository collectionRepository;
    private readonly CollectionState collectionState;

    public CharacterContextService(
        IClientState clientState,
        IPlayerState playerState,
        ICollectionRepository collectionRepository,
        CollectionState collectionState)
    {
        this.clientState = clientState;
        this.playerState = playerState;
        this.collectionRepository = collectionRepository;
        this.collectionState = collectionState;

        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;

        Refresh();
    }

    public string? CurrentCharacterKey { get; private set; }

    public bool HasCharacter => CurrentCharacterKey is not null;

    public void Refresh()
    {
        if (!clientState.IsLoggedIn || !playerState.IsLoaded || playerState.ContentId == 0)
        {
            CurrentCharacterKey = null;
            collectionState.Clear();
            return;
        }

        CurrentCharacterKey = CreateCharacterKey(playerState.ContentId);
        var displayName = string.IsNullOrWhiteSpace(playerState.CharacterName)
            ? null
            : $"{playerState.CharacterName}@{playerState.HomeWorld.RowId}";
        collectionRepository.UpsertCharacter(CurrentCharacterKey, displayName, playerState.HomeWorld.RowId);
        collectionState.Reload(CurrentCharacterKey, collectionRepository);
    }

    public void Dispose()
    {
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
    }

    public static string CreateCharacterKey(ulong contentId)
    {
        return $"content:{contentId:X16}";
    }

    private void OnLogin()
    {
        Refresh();
    }

    private void OnLogout(int type, int code)
    {
        CurrentCharacterKey = null;
        collectionState.Clear();
    }
}
