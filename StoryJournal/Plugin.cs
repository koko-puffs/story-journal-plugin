using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using StoryJournal.Session;
using StoryJournal.UI;

namespace StoryJournal;

/// <summary>
/// Story Journal: writes down your journey through the main story as you play, for kokos-tools.com
/// (Story Journal) to turn into recaps, a Codex and a storybook, and makes coming back easy in the game.
/// /sj opens the journal; /sj line saves the line on screen, /sj photo takes a photo without the HUD,
/// /sj note and /sj theory keep your words, /sj end ends the session, /sj pause and /sj resume, /sj settings.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/sj";
    private const string LongCommand = "/storyjournal";

    private readonly Configuration config;
    private readonly Journal journal;
    private readonly WindowSystem windows = new("StoryJournal");
    private readonly MainWindow main;
    private readonly WelcomeWindow welcome;
    private readonly TrackerWindow tracker;
    private readonly ArrivalWindow arrival;
    private readonly WhoWindow who;
    private readonly ToastWindow toasts;
    private readonly WrapUpWindow wrapUp;
    private readonly MilestoneWindow milestone;
    private readonly QuickNoteWindow quickNote;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();
        config = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        try
        {
            journal = new Journal(config);
            main = new MainWindow(journal);
            welcome = new WelcomeWindow(journal) { Open = main.ShowTab };
            tracker = new TrackerWindow(journal) { Open = main.ShowMain };
            arrival = new ArrivalWindow(journal) { OpenCodex = main.ShowCodex };
            who = new WhoWindow(journal) { OpenCodex = main.ShowCodex };
            toasts = new ToastWindow(journal);
            wrapUp = new WrapUpWindow(journal);
            milestone = new MilestoneWindow(journal);
            quickNote = new QuickNoteWindow(journal);
            main.OpenNote = quickNote.Show;
            journal.NoteAsked += () => quickNote.Show();
            windows.AddWindow(main);
            windows.AddWindow(welcome);
            windows.AddWindow(tracker);
            windows.AddWindow(arrival);
            windows.AddWindow(who);
            windows.AddWindow(wrapUp);
            windows.AddWindow(milestone);
            windows.AddWindow(quickNote);
            windows.AddWindow(toasts);

            Services.Commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open the journal. /sj line saves the line on screen, /sj photo takes a photo, /sj note <text> and /sj theory <text> keep your words, "
                              + "/sj end ends the session, /sj pause and /sj resume, /sj settings.",
            });
            Services.Commands.AddHandler(LongCommand, new CommandInfo(OnCommand) { HelpMessage = "The same as /sj.", ShowInHelp = false });
            Services.PluginInterface.UiBuilder.Draw += windows.Draw;
            Services.PluginInterface.UiBuilder.OpenMainUi += main.ShowMain;
            Services.PluginInterface.UiBuilder.OpenConfigUi += main.ShowSettings;
            Services.Framework.Update += OnUpdate;
        }
        catch
        {
            // Dalamud doesn't dispose a plugin whose constructor threw: what was hooked into the game is let go of here.
            Dispose();
            throw;
        }
    }

    private void OnCommand(string command, string args)
    {
        var text = args.Trim();
        var space = text.IndexOf(' ');
        var verb = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        switch (verb)
        {
            case "line":
                journal.SaveLine();
                break;
            case "photo":
                journal.TakePhoto(rest);
                break;
            case "note":
                if (rest.Length > 0) journal.AddNote(rest);
                else quickNote.Show();
                break;
            case "theory":
                if (rest.Length > 0) journal.SealTheory(rest, 10);
                else main.ShowTab(MainTab.Theories);
                break;
            case "end":
                journal.EndSession();
                break;
            case "pause":
                journal.Pause(true);
                break;
            case "resume":
                journal.Pause(false);
                break;
            case "settings":
            case "config":
                main.ShowSettings();
                break;
            case "welcome":
                journal.ShowWelcome();
                break;
            case "":
                main.Toggle();
                break;
            default:
                Services.Chat.Print("Story Journal: /sj, /sj line, /sj photo, /sj note <text>, /sj theory <text>, /sj end, /sj pause, /sj resume, /sj settings.");
                break;
        }
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try
        {
            journal.Tick();
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Story Journal's update failed");
        }
    }

    public void Dispose()
    {
        Safely(() => Services.Framework.Update -= OnUpdate);
        Safely(() => Services.PluginInterface.UiBuilder.Draw -= windows.Draw);
        if (main != null)
        {
            Safely(() => Services.PluginInterface.UiBuilder.OpenMainUi -= main.ShowMain);
            Safely(() => Services.PluginInterface.UiBuilder.OpenConfigUi -= main.ShowSettings);
        }
        Safely(() => Services.Commands.RemoveHandler(Command));
        Safely(() => Services.Commands.RemoveHandler(LongCommand));
        Safely(windows.RemoveAllWindows);
        Safely(() => main?.Dispose());
        Safely(() => welcome?.Dispose());
        Safely(() => tracker?.Dispose());
        Safely(() => arrival?.Dispose());
        Safely(() => who?.Dispose());
        Safely(() => toasts?.Dispose());
        Safely(() => wrapUp?.Dispose());
        Safely(() => milestone?.Dispose());
        Safely(() => quickNote?.Dispose());
        // What's under way is kept: the queue's on disk, and the open session is picked up again after a reload.
        Safely(() => journal?.Dispose());
    }

    private static void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Story Journal couldn't let go of something while unloading");
        }
    }
}
