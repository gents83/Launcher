using Launcher.Core.Configuration;

namespace Launcher.App.Settings;

public interface ITeaBoxProjectSettingsStore
{
    TeaBoxProjectSettingsSnapshot Load(ProjectConfig project);

    TeaBoxProjectSettingsSnapshot Save(ProjectConfig project, TeaBoxProjectSettingsSaveRequest request);
}
