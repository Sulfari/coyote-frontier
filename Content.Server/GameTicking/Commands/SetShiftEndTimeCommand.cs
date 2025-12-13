using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Timing;

namespace Content.Server.GameTicking.Commands
{
    [AdminCommand(AdminFlags.Round)]
    sealed class SetShiftEndTimeCommand : IConsoleCommand
    {
        [Dependency] private readonly IEntityManager _e = default!;
        [Dependency] private readonly IGameTiming _timing = default!;

        public string Command => "setshiftendtime";
        public string Description => "Sets the shift end time in hours from now or from round start.";
        public string Help => "setshiftendtime <hours> [now|roundstart] - Sets when the shift should end. Defaults to 'now'. Use 0 to clear.";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            var ticker = _e.System<GameTicker>();

            if (ticker.RunLevel != GameRunLevel.InRound)
            {
                shell.WriteLine("This can only be executed while the game is in a round.");
                return;
            }

            if (args.Length < 1)
            {
                shell.WriteError("Expected at least 1 argument.");
                shell.WriteLine(Help);
                return;
            }

            if (!double.TryParse(args[0], out var hours))
            {
                shell.WriteError("Invalid number format for hours.");
                return;
            }

            if (hours <= 0)
            {
                ticker.ShiftEndTime = null;
                shell.WriteLine("Shift end time cleared.");
                return;
            }

            // Determine mode: "now" (default) or "roundstart"
            var mode = args.Length > 1 ? args[1].ToLower() : "now";
            TimeSpan endTime;

            if (mode == "roundstart")
            {
                // Calculate from round start time
                // Round start in real time = current real time - (current game time - round start game time)
                var roundStartRealTime = _timing.RealTime - (_timing.CurTime - ticker.RoundStartTimeSpan);
                endTime = roundStartRealTime + TimeSpan.FromHours(hours);
                shell.WriteLine($"Shift end time set to {hours} hours from round start (server real time: {endTime}).");
            }
            else // "now" or any other value defaults to "now"
            {
                // Use RealTime to avoid drift issues during long shifts
                endTime = _timing.RealTime + TimeSpan.FromHours(hours);
                shell.WriteLine($"Shift end time set to {hours} hours from now (server real time: {endTime}).");
            }

            ticker.ShiftEndTime = endTime;
        }
    }
}
