namespace RefundDesk.Domain;

/// <summary>Which evaluator produced an <see cref="Evaluation"/>: code checks or the model rubric.</summary>
public static class EvaluationKind
{
    public const string Deterministic = "deterministic";
    public const string Model = "model";
}
