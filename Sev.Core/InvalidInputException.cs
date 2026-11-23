namespace Sev.Core;

// Input that can't be used, such as a date that can't be read or a period that's too long. The message says what to
// fix, in words a person or Claude can act on. The MCP tools pass it on as an McpException; a web page can show it.
public class InvalidInputException(string message) : Exception(message);
