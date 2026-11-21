namespace Sev.Client.Models;

public record Customer(
    string CustomerName,
    IReadOnlyList<Installation> Installations
);