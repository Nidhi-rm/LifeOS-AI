namespace LifeOS.Api.Models;

public class UserCredential
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
}
