using System;
using System.Threading.Tasks;

namespace IPK25_CHAT
{
    public enum MessageType
    {
        Message,
        Reply,
        Error,
        Bye
    }

    public class MessageReceivedEventArgs : EventArgs
    {
        public MessageType Type { get; }
        public string? Content { get; }
        public string? DisplayName { get; }

        public MessageReceivedEventArgs(MessageType type, string? content, string? displayName = null)
        {
            Type = type;
            Content = content;
            DisplayName = displayName;
        }
    }

    public class ErrorEventArgs : EventArgs
    {
        public string? Message { get; }
        public Exception? Exception { get; }

        public ErrorEventArgs(string? message, Exception? exception = null)
        {
            Message = message;
            Exception = exception;
        }
    }

    public interface IChatProtocol
    {
        event EventHandler<MessageReceivedEventArgs> MessageReceived;
        event EventHandler<ErrorEventArgs> Error;

        Task SendAuthAsync(string? username, string? displayName, string? secret);
        Task SendJoinAsync(string? channel, string? displayName);
        Task SendMessageAsync(string? displayName, string message);
        Task SendByeAsync(string? displayName);
        Task DisconnectAsync();
    }
} 