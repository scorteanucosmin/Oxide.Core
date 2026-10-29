using System;

namespace Oxide.Core.Libraries.Covalence
{
    public class CommandAlreadyExistsException : Exception
    {
        public CommandAlreadyExistsException()
        {
        }
        public CommandAlreadyExistsException(string cmd) : base($"Command {cmd} already exists")
        {
        }
        public CommandAlreadyExistsException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
