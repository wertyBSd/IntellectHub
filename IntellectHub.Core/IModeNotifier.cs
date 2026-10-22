namespace IntellectHub.Client;

/// <summary>
/// Интерфейс для уведомлений о смене мода. Реализуется клиентом (UI), а не Core.
/// Core не должен зависеть от Spectre.Console или любого другого UI-фреймворка.
/// </summary>
public interface IModeNotifier
{
    /// <summary>Сообщить о смене текущего режима.</summary>
    void OnModeChanged(string modeDisplay);

    /// <summary>Сообщить пользователю информационное сообщение.</summary>
    void OnAnnounce(string text);
}
