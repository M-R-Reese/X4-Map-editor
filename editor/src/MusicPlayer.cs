using System;
using System.Threading.Tasks;
using Godot;
using X4Data;
using X4Data.Audio;

namespace GalaxyEditor3D;

/// <summary>
/// Plays the game's music ids the way the game does: a track picked by the sound library's weights, then the next.
/// The .ogg is read from the archives in the background (tracks are about 10 MB).
/// </summary>
public partial class MusicPlayer : AudioStreamPlayer
{
    MusicLibrary? _lib;
    GameData? _game;
    readonly Random _rnd = new();
    string? _lastFile;
    int _request;                       // newer requests cancel older loads
    /// <summary>Music id playing now, or null.</summary>
    public string? Current { get; private set; }
    public event Action? StateChanged;

    public override void _Ready() { Finished += () => { if (Current != null) Next(); }; VolumeDb = -6; }

    public void SetGame(GameData g) { _game = g; _lib = new MusicLibrary(g); }
    public MusicLibrary? Library => _lib;

    public void Play(string musicId)
    {
        if (_lib?.Get(musicId) == null) { Stop(); return; }
        if (Current == musicId && Playing) return;
        Current = musicId; _lastFile = null;
        Next();
        StateChanged?.Invoke();
    }

    public new void Stop()
    {
        Current = null; _request++;
        base.Stop();
        StateChanged?.Invoke();
    }

    async void Next()
    {
        var music = Current != null ? _lib?.Get(Current) : null;
        if (music == null || _game == null) return;
        var track = MusicLibrary.Pick(music, _rnd, _lastFile);
        int req = ++_request;
        byte[] bytes;
        try { bytes = await Task.Run(() => _game.Vfs.Read(track.File)); }
        catch (Exception e) { GD.PrintErr("music: " + e.Message); return; }
        if (req != _request) return;                              // stopped or switched while loading
        // some game tracks have an old-style comment ("Sony Ogg Vorbis 1.0 Final"): Godot warns once, the track plays fine
        var stream = AudioStreamOggVorbis.LoadFromBuffer(bytes);
        if (stream == null) { GD.PrintErr("music: not playable " + track.File); return; }
        stream.Loop = false;
        Stream = stream; _lastFile = track.File;
        base.Play();
        GD.Print($"music: {Current} -> {track.File} ({stream.GetLength():F0} s)");
    }
}
