namespace ListenerSound.Common;

public static class Protocol
{
    public const string RegisterPrefix = "REGISTER:";
    public const string TriggerCommand = "TRIGGER";
    public const string ByeCommand = "BYE";
    public const string OkPrefix = "OK";
    public const string ErrorPrefix = "ERROR";

    // Formato de registro: REGISTER:<token>:<id>
    public static string BuildRegister(string token, string id)
        => $"{RegisterPrefix}{token}:{id}";

    // Devuelve (Token, Id). Token vacío si la línea no es un registro válido.
    public static (string? Token, string? Id) ParseRegister(string line)
    {
        if (!line.StartsWith(RegisterPrefix)) return (null, null);
        var rest = line[RegisterPrefix.Length..];
        var sep = rest.IndexOf(':');
        if (sep < 0) return (null, null);
        return (rest[..sep], rest[(sep + 1)..]);
    }
}
