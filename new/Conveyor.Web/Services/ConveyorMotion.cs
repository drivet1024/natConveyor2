using Conveyor.Web.Options;

namespace Conveyor.Web.Services;

public sealed record ConveyorActionResult(bool Recorded, string Message);

public static class ConveyorMotion
{
    public const string DefaultMotionTag = GeneralOptions.DefaultConveyorStartTag;

    public static async Task<ConveyorActionResult> ExecuteAsync(IPlcGateway plc, IConveyorRepository repository,
        int? conveyorId, bool simulation, bool start, int? cause, ILogger logger, string motionTag = DefaultMotionTag,
        Func<Task>? commandSent = null)
    {
        if (conveyorId is null or < 0) throw new InvalidOperationException("Configurer Conveyor ID dans les paramètres globaux, puis redémarrer l’application.");
        if (start ? cause is not null : cause is not (0 or 1 or 2))
            throw new InvalidOperationException("Choisir une cause d’arrêt : PAUSE, JAM ou DOWN.");
        if (!plc.IsConnected) throw new InvalidOperationException("Automate déconnecté. Utiliser CONNECTER dans l’engrenage de la ligne principale.");
        if (!simulation && repository.IsSimulation) throw new InvalidOperationException("Configurer la base MySQL pour enregistrer les actions.");
        if (string.IsNullOrWhiteSpace(motionTag)) throw new InvalidOperationException("Configurer le tag de démarrage du convoyeur.");
        motionTag = motionTag.Trim();
        // One shared command starts or stops both lines.
        await plc.SendChuteAsync(motionTag, start ? 1 : 0, 1, CancellationToken.None);
        if (commandSent is not null) await commandSent();
        if (simulation) return new(true, "Commande simulée ; aucun mouvement ni enregistrement MySQL.");
        try
        {
            await repository.SaveConveyorActionAsync(conveyorId.Value, start, cause, CancellationToken.None);
            logger.LogInformation("Convoyeur {ConveyorId} : commande {MotionTag}={Action} envoyée et enregistrée, cause {Cause}", conveyorId, motionTag, start ? 1 : 0, cause);
            return new(true, "Commande envoyée à l’automate et enregistrée.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Convoyeur {ConveyorId} : commande {MotionTag}={Action} envoyée mais enregistrement conveyor_action échoué", conveyorId, motionTag, start ? 1 : 0);
            return new(false, "Commande envoyée à l’automate, mais l’enregistrement dans conveyor_action a échoué. Consulter les journaux.");
        }
    }
}
