using TelegramAutoDeleteBot;
using TelegramAutoDeleteBot.Models;
using TelegramAutoDeleteBot.Services;

var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
var statePath = Path.Combine(AppContext.BaseDirectory, "state.json");

// Load settings (required) and state (created if missing)
var settings = await JsonStore.LoadAsync<Settings>(settingsPath)
    ?? throw new InvalidOperationException($"Missing settings file at {settingsPath}");
var state = await JsonStore.LoadAsync<State>(statePath) ?? new State();

// Basic sanity: no token / no groups
if (string.IsNullOrWhiteSpace(settings.BotToken))
    throw new InvalidOperationException("settings.json missing BotToken");
if (settings.AllowedChatIds is null || settings.AllowedChatIds.Count == 0)
    Console.WriteLine("Warning: AllowedChatIds is empty — bot will ignore all chats.");

// Run bot service
await using var bot = new BotService(settings, state, statePath);
await bot.RunAsync();
