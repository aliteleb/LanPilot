namespace Divert.Windows;

/// <summary>
/// Represents the result of a Divert receive operation.
/// </summary>
/// <param name="dataLength">The length of the received data.</param>
/// <param name="addressLength">The length of the addresses.</param>
public readonly struct DivertReceiveResult(int dataLength, int addressLength)
{
    /// <summary>
    /// Gets the length of the received data.
    /// </summary>
    public int DataLength { get; } = dataLength;

    /// <summary>
    /// Gets the length of the addresses.
    /// </summary>
    public int AddressLength { get; } = addressLength;

    /// <summary>
    /// Deconstructs the result into its components.
    /// </summary>
    /// <param name="dataLength">The length of the received data.</param>
    /// <param name="addressLength">The length of the addresses.</param>
    public void Deconstruct(out int dataLength, out int addressLength)
    {
        dataLength = DataLength;
        addressLength = AddressLength;
    }
}
