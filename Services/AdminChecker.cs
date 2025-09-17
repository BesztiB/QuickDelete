using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace TelegramAutoDeleteBot.Services;

public static class AdminChecker
{
    public static async Task<bool> IsChatAdminAsync(ITelegramBotClient bot, long chatId, long? userId)
    {
        if (userId is null) return false;

        try
        {
            var member = await bot.GetChatMemberAsync(chatId, userId.Value);
            return member.Status is ChatMemberStatus.Creator or ChatMemberStatus.Administrator;
        }
        catch
        {
            return false;
        }
    }
}
