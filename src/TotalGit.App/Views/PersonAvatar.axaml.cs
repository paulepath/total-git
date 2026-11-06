using Avalonia.Controls;
using TotalGit.App.ViewModels;

namespace TotalGit.App.Views;

/// <summary>
/// A person's round avatar, or their initials on their colour until it loads (or when they have none). Right-click
/// to choose a picture for them.
/// </summary>
public partial class PersonAvatar : UserControl
{
    public PersonAvatar()
    {
        InitializeComponent();
        ContextRequested += (_, e) =>
        {
            if (DataContext is not PersonViewModel { Avatars: { } avatars } person || person.User.IsBot) return;
            e.Handled = true;
            var who = new PersonRef(person.Name, Login: person.Login, AvatarUrl: person.User.AvatarUrl);
            var first = person.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? person.Name;
            var set = new MenuItem { Header = $"Set picture for {first}…" };
            set.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is not Window owner) return;
                var dialog = new PersonPictureDialog { DataContext = new PersonPictureViewModel(avatars, who, person.Avatar) };
                await dialog.ShowDialog<bool>(owner);
            };
            List<MenuItem> items = [set];
            if (avatars.Custom.Find(null, person.Login) is { } chosen)
            {
                var remove = new MenuItem { Header = "Remove custom picture" };
                remove.Click += (_, _) => avatars.Custom.Remove(chosen.Id);
                items.Add(remove);
            }
            new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer }.Open(this);
        };
    }
}
