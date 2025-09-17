using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Exceptions;
using TelegramAutoDeleteBot.Models;
using TelegramAutoDeleteBot.Services;
using System.Collections.Concurrent;

namespace TelegramAutoDeleteBot;

public sealed class BotService : IAsyncDisposable
{
    private readonly ITelegramBotClient _bot;
    private readonly Settings _settings;
    private readonly State _state;
    private readonly string _statePath;

    private readonly CancellationTokenSource _cts = new();
    private readonly PeriodicTimer _sweeper = new(TimeSpan.FromSeconds(5));
    private readonly object _stateLock = new();

    public BotService(Settings settings, State state, string statePath)
    {
        _settings = settings;
        _state = state;
        _statePath = statePath;
        _bot = new TelegramBotClient(settings.BotToken);

        // Normalize containers
        _state.TopicPolicies ??= new();
        _state.Scheduled ??= new();
    }

    public async Task RunAsync()
    {
        var me = await _bot.GetMeAsync();
        Console.WriteLine($"Bot started: @{me.Username} (id {me.Id})");

        // Receiver
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[]
            {
                UpdateType.Message,
                UpdateType.EditedMessage,
                UpdateType.ChannelPost,
                UpdateType.EditedChannelPost,
                UpdateType.MyChatMember
            },
            ThrowPendingUpdates = true
        };

        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, _cts.Token);

        // Background sweeper loop
        _ = Task.Run(DeleteDueMessagesLoop, _cts.Token);

        Console.WriteLine("Press Ctrl+C to exit.");
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
        };

        // Wait until cancelled
        try { await Task.Delay(Timeout.Infinite, _cts.Token); }
        catch (TaskCanceledException) { /* normal */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _sweeper.Dispose();
        try { await JsonStore.SaveAsync(_statePath, _state); } catch { /* ignore */ }
        _cts.Dispose();
    }

    private Task HandleErrorAsync(ITelegramBotClient _, Exception ex, CancellationToken __)
    {
        var msg = ex switch
        {
            ApiRequestException apiEx => $"Telegram API Error: [{apiEx.ErrorCode}] {apiEx.Message}",
            _ => ex.ToString()
        };
        Console.WriteLine(msg);
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Type != UpdateType.Message || update.Message is null) return;
            var msg = update.Message;

            // Only react to groups/supergroups listed in settings
            if (msg.Chat.Type is not ChatType.Supergroup and not ChatType.Group) return;
            if (!_settings.AllowedChatIds.Contains(msg.Chat.Id)) return;

            // Commands (only from admins)
            if (msg.Text is not null && msg.Text.StartsWith("/"))
            {
                await HandleCommandAsync(msg, ct);
                return;
            }

            // Pinned-message event => exclude from deletion if we had it scheduled
            if (msg.PinnedMessage is not null)
            {
                var pinned = msg.PinnedMessage;
                RemoveScheduled(pinned.Chat.Id, pinned.MessageId);
                await PersistStateAsync();
                return;
            }

            // Schedule normal messages for auto-delete if policy active in the topic
            await MaybeScheduleDeleteAsync(msg, ct);
            await MaybeDeleteOnMaxMessagesAsync(msg, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Update handler error: " + ex);
        }
    }

    private async Task HandleCommandAsync(Message msg, CancellationToken ct)
    {
        var text = msg.Text!.Trim();
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();

        if (cmd is "/autodelete" or "/autodelete@")
        {
            // Check admin status
            var isAdmin = await AdminChecker.IsChatAdminAsync(_bot, msg.Chat.Id, msg.From?.Id);
            if (!isAdmin)
            {
                await _bot.SendTextMessageAsync(msg.Chat.Id,
                    "Only group admins can change auto-delete settings.",
                    messageThreadId: msg.MessageThreadId,
                    replyToMessageId: msg.MessageId,
                    cancellationToken: ct);
                return;
            }

            if (parts.Length < 2 || !int.TryParse(parts[1], out var minutes) || minutes < 0)
            {
                await _bot.SendTextMessageAsync(msg.Chat.Id,
                    "Usage: `/autodelete <minutes>` (use 0 to disable for this topic)",
                    parseMode: ParseMode.Markdown,
                    messageThreadId: msg.MessageThreadId,
                    replyToMessageId: msg.MessageId,
                    cancellationToken: ct);
                return;
            }

            var key = TopicKey(msg.Chat.Id, msg.MessageThreadId);
            lock (_stateLock)
            {
                if (!_state.TopicPolicies.TryGetValue(key, out var policy))
                {
                    policy = new TopicPolicy { Minutes = 0, MaxMessages = 0 };
                    _state.TopicPolicies[key] = policy;
                }
                policy.Minutes = minutes;
                if (minutes == 0 && policy.MaxMessages == 0)
                {
                    // Remove policy if both are zero
                    _state.TopicPolicies.Remove(key);
                }
            }

            await PersistStateAsync();

            var reply = minutes == 0
                ? "Auto-delete disabled for this topic."
                : $"Auto-delete set to *{minutes} minute(s)* for this topic.";
            await _bot.SendTextMessageAsync(msg.Chat.Id, reply,
                parseMode: ParseMode.Markdown,
                messageThreadId: msg.MessageThreadId,
                replyToMessageId: msg.MessageId,
                cancellationToken: ct);
        }

        if (cmd is "/deletemaxmessages" or "/deletemaxmessages@")
        {
            // Check admin status
            var isAdmin = await AdminChecker.IsChatAdminAsync(_bot, msg.Chat.Id, msg.From?.Id);
            if (!isAdmin)
            {
                await _bot.SendTextMessageAsync(msg.Chat.Id,
                    "Only group admins can change auto-delete settings.",
                    messageThreadId: msg.MessageThreadId,
                    replyToMessageId: msg.MessageId,
                    cancellationToken: ct);
                return;
            }

            if (parts.Length < 2 || !int.TryParse(parts[1], out var maxMessages) || maxMessages < 0)
            {
                await _bot.SendTextMessageAsync(msg.Chat.Id,
                    "Usage: `/deletemaxmessages <max messages>` (use 0 to disable for this topic)",
                    parseMode: ParseMode.Markdown,
                    messageThreadId: msg.MessageThreadId,
                    replyToMessageId: msg.MessageId,
                    cancellationToken: ct);
                return;
            }

            var key = TopicKey(msg.Chat.Id, msg.MessageThreadId);
            lock (_stateLock)
            {
                if (!_state.TopicPolicies.TryGetValue(key, out var policy))
                {
                    policy = new TopicPolicy { Minutes = 0, MaxMessages = 0 };
                    _state.TopicPolicies[key] = policy;
                }
                policy.MaxMessages = maxMessages;
                if (maxMessages == 0 && policy.Minutes == 0)
                {
                    // Remove policy if both are zero
                    _state.TopicPolicies.Remove(key);
                }
            }

            await PersistStateAsync();

            var reply = maxMessages == 0
                ? "Auto-delete Messages disabled for this topic."
                : $"Auto-delete set to *{maxMessages} message(s)* for this topic.";
            await _bot.SendTextMessageAsync(msg.Chat.Id, reply,
                parseMode: ParseMode.Markdown,
                messageThreadId: msg.MessageThreadId,
                replyToMessageId: msg.MessageId,
                cancellationToken: ct);
        }
    }

    private async Task MaybeScheduleDeleteAsync(Message msg, CancellationToken ct)
    {
        // Skip service/system messages
        if (msg.Date == default) return;

        // Skip pinned (if somehow flagged here)
        if (msg.IsAutomaticForward ?? false) return; // optional: ignore auto-forwards
        if (msg.ForwardFromChat is not null || msg.ForwardFrom is not null) { /* up to you */ }

        // Respect topic policy
        var key = TopicKey(msg.Chat.Id, msg.MessageThreadId);
        TopicPolicy? policy;
        lock (_stateLock)
        {
            if (!_state.TopicPolicies.TryGetValue(key, out policy) || policy.Minutes <= 0) return;
        }

        // Don't schedule if the message is pinned at post time (rare)
        if (msg.IsTopicMessage == true && msg.PinnedMessage is not null) return;

        // Schedule deletion
        var due = msg.Date.ToUniversalTime().AddMinutes(policy.Minutes);
        var rec = new ScheduledDeletion
        {
            ChatId = msg.Chat.Id,
            MessageId = msg.MessageId,
            MessageThreadId = msg.MessageThreadId,
            DeleteAtUtc = due
        };

        lock (_stateLock)
        {
            _state.Scheduled.RemoveAll(x => x.ChatId == rec.ChatId && x.MessageId == rec.MessageId);
            _state.Scheduled.Add(rec);
        }

        await PersistStateAsync();
    }

    private async Task MaybeDeleteOnMaxMessagesAsync(Message msg, CancellationToken ct)
    {
        // Skip service/system messages
        if (msg.Date == default) return;

        // Respect topic policy
        var key = TopicKey(msg.Chat.Id, msg.MessageThreadId);
        TopicPolicy? policy;
        lock (_stateLock)
        {
            if (!_state.TopicPolicies.TryGetValue(key, out policy) || policy.MaxMessages <= 0) return;
        }

        List<StoredMessage> toDelete = new();
        lock (_stateLock)
        {
            if (!_state.StoredMessages.TryGetValue(key, out var list))
            {
                list = new List<StoredMessage>();
                _state.StoredMessages[key] = list;
            }

            list.Add(new StoredMessage
            {
                ChatId = msg.Chat.Id,
                MessageId = msg.MessageId,
                MessageThreadId = msg.MessageThreadId
            });

            while (list.Count > policy.MaxMessages)
            {
                var oldest = list[0];
                list.RemoveAt(0);
                toDelete.Add(oldest);
            }
        }

        if (toDelete.Count == 0) return;

        foreach (var item in toDelete)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await _bot.DeleteMessageAsync(item.ChatId, item.MessageId, ct);
            }
            catch (ApiRequestException apiEx)
            {
                // Common benign cases: message already deleted, not found, too old, insufficient rights
                Console.WriteLine($"Delete failed [{apiEx.ErrorCode}]: {apiEx.Message} ({item.ChatId}/{item.MessageId})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Delete error: {ex.Message} ({item.ChatId}/{item.MessageId})");
            }
            finally
            {
                RemoveScheduled(item.ChatId, item.MessageId);
            }
        }

        await PersistStateAsync();
    }

    private async Task DeleteDueMessagesLoop()
    {
        while (await _sweeper.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
        {
            List<ScheduledDeletion> due;
            var now = DateTime.UtcNow;

            lock (_stateLock)
            {
                due = _state.Scheduled
                    .Where(x => x.DeleteAtUtc <= now)
                    .Take(50) // batch safety
                    .ToList();
            }

            if (due.Count == 0) continue;

            foreach (var item in due)
            {
                if (_cts.IsCancellationRequested) break;

                try
                {
                    // Remove from stored messages too (should be there, but just in case)
                    foreach (var list in _state.StoredMessages.Values)
                    {
                        list.RemoveAll(x => x.ChatId == item.ChatId && x.MessageId == item.MessageId);
                    }

                    // Try delete; if message got pinned after scheduling, a pin event should have removed it already.
                    await _bot.DeleteMessageAsync(item.ChatId, item.MessageId, _cts.Token);
                }
                catch (ApiRequestException apiEx)
                {
                    // Common benign cases: message already deleted, not found, too old, insufficient rights
                    Console.WriteLine($"Delete failed [{apiEx.ErrorCode}]: {apiEx.Message} ({item.ChatId}/{item.MessageId})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Delete error: {ex.Message} ({item.ChatId}/{item.MessageId})");
                }
                finally
                {
                    RemoveScheduled(item.ChatId, item.MessageId);
                }
            }

            await PersistStateAsync();
        }
    }

    private void RemoveScheduled(long chatId, int messageId)
    {
        lock (_stateLock)
        {
            _state.Scheduled.RemoveAll(x => x.ChatId == chatId && x.MessageId == messageId);
            foreach (var list in _state.StoredMessages.Values)
            {
                list.RemoveAll(x => x.ChatId == chatId && x.MessageId == messageId);
            }
        }
    }

    private static string TopicKey(long chatId, int? threadId)
        => $"{chatId}:{threadId?.ToString() ?? "0"}";

    private async Task PersistStateAsync()
    {
        try
        {
            // Save atomically
            await JsonStore.SaveAsync(_statePath, _state);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to save state: " + ex.Message);
        }
    }
}
