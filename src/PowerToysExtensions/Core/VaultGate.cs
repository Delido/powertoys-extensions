using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.System;

namespace PowerToysExtensions.Core;

/// <summary>
/// Setup and unlock flow of a <see cref="Vault"/> inside list pages: while not set up or locked, a list
/// shows these items instead of its content.
/// </summary>
public sealed class VaultGate(Vault vault, string product, string secretLabel, string secretHelp, Func<string, Task> verify)
{
    public Vault Vault => vault;

    /// <summary>Items to show instead of the content, or null when unlocked.</summary>
    public IListItem[]? Items(Action reload)
    {
        if (!vault.IsSetUp)
        {
            return [new ListItem(SetupPage(reload)) { Title = $"Set Up {product}", Subtitle = $"Store the {secretLabel} encrypted with a password of your choice", Icon = Ui.Glyph("") }];
        }

        if (vault.IsUnlocked)
        {
            return null;
        }

        var items = new List<IListItem>();
        if (vault.HelloEnabled)
        {
            items.Add(new ListItem(new AnonymousCommand(() => UnlockWithHello(reload)) { Name = "Unlock", Result = CommandResult.KeepOpen() })
            {
                Title = "Unlock with Windows Hello",
                Subtitle = $"🔒 {product} is locked",
                Icon = Ui.Glyph(""),
            });
        }

        items.Add(new ListItem(UnlockPage(reload))
        {
            Title = "Unlock with Password",
            Subtitle = vault.HelloEnabled ? string.Empty : $"🔒 {product} is locked",
            Icon = Ui.Glyph(""),
        });
        items.Add(new ListItem(ResetCommand(reload).Command!) { Title = "Reset (Forgot Password)", Subtitle = $"Deletes the stored {secretLabel}", Icon = Ui.Glyph("") });
        return [.. items];
    }

    /// <summary>Lock, change password, Windows Hello and reset – for the actions of every item.</summary>
    public IContextItem[] SecurityActions(Action reload)
    {
        var helloForm = new FormBuilder()
            .Text($"Windows Hello can unlock {product} instead of the password. Enter the password once; Windows asks for your PIN, fingerprint or face.", subtle: true)
            .Input("password", "Password", password: true, required: true)
            .Build("Enable Windows Hello");
        return
        [
            Ui.Action($"Lock {product}", "", () =>
            {
                vault.LockNow();
                Status.Info($"🔒 {product} locked");
                reload();
            }, Ui.Ctrl(VirtualKey.L)),
            new CommandContextItem(ChangePasswordPage()) { Title = "Change Password" },
            vault.HelloEnabled
                ? Ui.Action("Disable Windows Hello", "", () =>
                {
                    vault.DisableHello();
                    Status.Info("Windows Hello unlock disabled");
                })
                : new CommandContextItem(new FormPage("Enable Windows Hello", helloForm, inputs =>
                {
                    var password = inputs.Str("password");
                    Status.Start("Waiting for Windows Hello …", $"{product} can now be unlocked with Windows Hello", () => vault.EnableHello(password));
                    return CommandResult.GoBack();
                }, Ui.Glyph(""))) { Title = "Enable Windows Hello" },
            ResetCommand(reload),
        ];
    }

    private void UnlockWithHello(Action reload) => _ = Task.Run(async () =>
    {
        try
        {
            if (await vault.UnlockWithHello())
            {
                reload();
            }
            else
            {
                Status.Error("Windows Hello could not unlock – use the password");
            }
        }
        catch (Exception ex)
        {
            Status.Error(ex.Message);
        }
    });

    private CommandContextItem ResetCommand(Action reload) =>
        Ui.Confirm("Reset (Forgot Password)", "", $"Reset {product} access?", $"The stored {secretLabel} is deleted. You need to enter it again and choose a new password.", () =>
        {
            vault.Reset();
            reload();
        }, critical: true);

    private FormPage SetupPage(Action reload)
    {
        var form = new FormBuilder()
            .Text($"Your {secretLabel} is stored encrypted with a password of your choice. {product} content is only accessible after unlocking, and locks again automatically. Windows Hello can be enabled afterwards (Ctrl+K on any entry).", subtle: true)
            .Input("secret", secretLabel, placeholder: secretHelp, password: true, required: true)
            .Input("password", "Password", placeholder: $"At least {Vault.MinPassword} characters", password: true, required: true)
            .Input("confirm", "Repeat Password", password: true, required: true)
            .Text("There is no recovery: if you forget the password, reset and enter the secret again.", subtle: true)
            .Build("Save and Unlock");
        return new FormPage($"Set Up {product}", form, inputs =>
        {
            var secret = inputs.Str("secret");
            var password = inputs["password"]?.GetValue<string>() ?? string.Empty;
            if (secret.Length == 0)
            {
                return Toast($"{secretLabel} is required");
            }

            if (password.Length < Vault.MinPassword)
            {
                return Toast($"The password needs at least {Vault.MinPassword} characters");
            }

            if (password != inputs["confirm"]?.GetValue<string>())
            {
                return Toast("Passwords don't match");
            }

            Status.Start($"Checking the {secretLabel} …", $"{product} is set up", async () =>
            {
                await verify(secret);
                vault.Create(secret, password);
            }, reload);
            return CommandResult.GoBack();
        }, Ui.Glyph(""));
    }

    private FormPage UnlockPage(Action reload)
    {
        var form = new FormBuilder()
            .Text($"🔒 Enter your {product} password to continue.", subtle: true)
            .Input("password", "Password", password: true, required: true)
            .Build("Unlock");
        return new FormPage($"{product} Locked", form, inputs =>
        {
            if (vault.LockoutRemaining > 0)
            {
                return Toast($"Too many attempts, try again in {vault.LockoutRemaining} s");
            }

            if (vault.Unlock(inputs["password"]?.GetValue<string>() ?? string.Empty))
            {
                reload();
                return CommandResult.GoBack();
            }

            return Toast(vault.LockoutRemaining > 0 ? $"Too many attempts, try again in {vault.LockoutRemaining} s" : "Wrong password");
        }, Ui.Glyph(""));
    }

    private FormPage ChangePasswordPage()
    {
        var form = new FormBuilder()
            .Input("old", "Current Password", password: true, required: true)
            .Input("password", "New Password", placeholder: $"At least {Vault.MinPassword} characters", password: true, required: true)
            .Input("confirm", "Repeat New Password", password: true, required: true)
            .Build("Change Password");
        return new FormPage("Change Password", form, inputs =>
        {
            var password = inputs["password"]?.GetValue<string>() ?? string.Empty;
            if (password.Length < Vault.MinPassword)
            {
                return Toast($"The password needs at least {Vault.MinPassword} characters");
            }

            if (password != inputs["confirm"]?.GetValue<string>())
            {
                return Toast("Passwords don't match");
            }

            if (!vault.ChangePassword(inputs["old"]?.GetValue<string>() ?? string.Empty, password))
            {
                return Toast("Wrong current password");
            }

            Status.Info("Password changed");
            return CommandResult.GoBack();
        }, Ui.Glyph(""));
    }

    private static CommandResult Toast(string message) => CommandResult.ShowToast(new ToastArgs { Message = message, Result = CommandResult.KeepOpen() });
}
