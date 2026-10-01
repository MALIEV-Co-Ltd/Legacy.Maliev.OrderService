namespace Legacy.Maliev.OrderService.Application.Interfaces;

public enum OrderDeletionResult { Deleted, NotFound, Unavailable, Conflict }

public interface IOrderDeletionRecovery
{
    Task<OrderDeletionResult> DeleteOrderWithRecoveryAsync(int id, CancellationToken cancellationToken);
    Task<int> RecoverPendingDeletionsAsync(int batchSize, CancellationToken cancellationToken, int maxBackoffSeconds = 300);
}

public sealed class OrderDeletionUnavailableException() : Exception("Order deletion is temporarily unavailable.");
public sealed class OrderDeletionConflictException() : Exception("Order lifetime conflicts with its deletion receipt.");
public sealed class OrderMutationUnavailableException() : Exception("Order mutation is temporarily unavailable.");
