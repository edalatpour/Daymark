namespace Ben.Services.Auth;

public interface IAuthenticationLifecycleCoordinator
{
    Task<bool> InitializeSignedInUserAsync();

    Task<bool> SignOutWithCleanupAsync();
}
