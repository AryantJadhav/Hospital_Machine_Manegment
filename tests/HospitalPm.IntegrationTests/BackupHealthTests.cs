using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Operations;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The backup banner's decision, with no database.
///
/// The dashboard tests share one database and other tests leave recent
/// backups in it, so the "nothing has ever succeeded" states cannot be reached
/// there. They matter most: a fresh install was shown a red "Backups are not
/// running" an hour after setup, before its first overnight backup was due.
/// </summary>
public sealed class BackupHealthTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_new_install_with_no_backup_yet_is_pending_not_a_problem()
    {
        var state = BackupHealth.Evaluate(null, null, installedAtUtc: Now.AddHours(-1), Now);

        Assert.Equal(BackupHealthState.Pending, state);
    }

    [Fact]
    public void A_new_install_is_still_pending_while_its_first_backup_is_running()
    {
        var state = BackupHealth.Evaluate(BackupStatus.Running, null, Now.AddHours(-3), Now);

        Assert.Equal(BackupHealthState.Pending, state);
    }

    [Fact]
    public void A_failed_first_attempt_is_a_problem_however_new_the_install_is()
    {
        var state = BackupHealth.Evaluate(BackupStatus.Failed, null, Now.AddHours(-1), Now);

        Assert.Equal(BackupHealthState.Problem, state);
    }

    [Fact]
    public void An_install_older_than_the_limit_with_no_backup_is_a_problem()
    {
        var state = BackupHealth.Evaluate(null, null, Now.AddHours(-49), Now);

        Assert.Equal(BackupHealthState.Problem, state);
    }

    [Fact]
    public void With_no_known_install_time_no_backup_is_a_problem()
    {
        // Better to say something than to stay silent on a database whose age
        // cannot be worked out.
        Assert.Equal(BackupHealthState.Problem, BackupHealth.Evaluate(null, null, null, Now));
    }

    [Fact]
    public void A_recent_good_backup_is_ok()
    {
        var state = BackupHealth.Evaluate(BackupStatus.Succeeded, Now.AddHours(-10), Now.AddDays(-30), Now);

        Assert.Equal(BackupHealthState.Ok, state);
    }

    [Fact]
    public void A_recent_good_backup_with_a_failed_newest_attempt_is_a_warning()
    {
        var state = BackupHealth.Evaluate(BackupStatus.Failed, Now.AddHours(-20), Now.AddDays(-30), Now);

        Assert.Equal(BackupHealthState.Warning, state);
    }

    [Fact]
    public void A_good_backup_older_than_the_limit_is_a_problem()
    {
        var state = BackupHealth.Evaluate(BackupStatus.Succeeded, Now.AddHours(-49), Now.AddDays(-30), Now);

        Assert.Equal(BackupHealthState.Problem, state);
    }
}
