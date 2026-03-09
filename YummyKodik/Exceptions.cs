namespace YummyKodik
{
    public class KodikException : Exception
    {
        public KodikException(string message) : base(message) { }
        public KodikException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class KodikTokenException : KodikException
    {
        public KodikTokenException(string message) : base(message) { }
        public KodikTokenException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class KodikServiceException : KodikException
    {
        public KodikServiceException(string message) : base(message) { }
        public KodikServiceException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class KodikNoResultsException : KodikException
    {
        public KodikNoResultsException(string message) : base(message) { }
        public KodikNoResultsException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class KodikDecryptionException : KodikException
    {
        public KodikDecryptionException(string message) : base(message) { }
        public KodikDecryptionException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class KodikUnexpectedException : KodikException
    {
        public KodikUnexpectedException(string message) : base(message) { }
        public KodikUnexpectedException(string message, Exception inner) : base(message, inner) { }
    }
}