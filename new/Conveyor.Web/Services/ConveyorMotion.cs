namespace Conveyor.Web.Services;

public sealed record ConveyorActionResult(bool Recorded, string Message);

public static class ConveyorMotion
{
    public static async Task<ConveyorActionResult> ExecuteAsync(IPlcGateway plc, IConveyorRepository repository,
        int? conveyorId, bool simulation, bool start, int? cause, ILogger logger)
    {
        if (conveyorId is null or < 0) throw new InvalidOperationException("Configurer Conveyor ID dans les paramètres globaux, puis redémarrer l’application.");
        if (start ? cause is not null : cause is not (0 or 1 or 2))
            throw new InvalidOperationException("Choisir une cause d’arrêt : PAUSE, JAM ou DOWN.");
        if (!plc.IsConnected) throw new InvalidOperationException("Automate déconnecté. Utiliser CONNECTER dans l’engrenage de la ligne principale.");
        if (!simulation && repository.IsSimulation) throw new InvalidOperationException("Configurer la base MySQL pour enregistrer les actions.");
        // Same command as DDEFrm: START=1 or START=0, once for both lines.
        await plc.SendChuteAsync("START", start ? 1 : 0, 1, CancellationToken.None);
        if (simulation) return new(true, "Commande simulée ; aucun mouvement ni enregistrement MySQL.");
        try
        {
            await repository.SaveConveyorActionAsync(conveyorId.Value, start, cause, CancellationToken.None);
            logger.LogInformation("Convoyeur {ConveyorId} : commande START={Action} envoyée et enregistrée, cause {Cause}", conveyorId, start ? 1 : 0, cause);
            return new(true, "Commande envoyée à l’automate et enregistrée.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Convoyeur {ConveyorId} : commande START={Action} envoyée mais enregistrement conveyor_action échoué", conveyorId, start ? 1 : 0);
            return new(false, "Commande envoyée à l’automate, mais l’enregistrement dans conveyor_action a échoué. Consulter les journaux.");
        }
    }
}
