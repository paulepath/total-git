using CommunityToolkit.Mvvm.Input;

namespace TotalGit.App.ViewModels;

// Pictures chosen for people (shown instead of their GitHub or Gravatar picture).
public partial class RepositoryViewModel
{
    /// <summary>"Set picture for…", and "Remove custom picture" when they have one.</summary>
    public IReadOnlyList<MenuAction> PictureActions(PersonRef person)
    {
        var first = person.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? person.Name;
        List<MenuAction> actions = [new($"Set picture for {first}…", SetPictureCommand, person, Icon: MenuIcons.Person)];
        if (Avatars.Custom.Find(person.Email, person.Login) is not null)
            actions.Add(new MenuAction("Remove custom picture", RemovePictureCommand, person));
        return actions;
    }

    [RelayCommand]
    private async Task SetPictureAsync(PersonRef person)
    {
        if (Dialogs is null) return;
        var current = person.Email is { } email
            ? await Avatars.GetAsync(email, Graph?.GitHubRepo, null)
            : await Avatars.GetUserAsync(person.Login, person.AvatarUrl);
        await Dialogs.ShowPersonPictureAsync(new PersonPictureViewModel(Avatars, person, current));
    }

    [RelayCommand]
    private void RemovePicture(PersonRef person)
    {
        if (Avatars.Custom.Find(person.Email, person.Login) is { } chosen) Avatars.Custom.Remove(chosen.Id);
    }
}
