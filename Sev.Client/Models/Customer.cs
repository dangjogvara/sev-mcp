namespace Sev.Client.Models;

public record Customer(
    string CustomerName,
    List<Installation> Installations
);