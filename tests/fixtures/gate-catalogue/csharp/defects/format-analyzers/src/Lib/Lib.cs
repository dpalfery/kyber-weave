namespace Lib;

public sealed class Calculator
{
    private readonly int _offset;

    public Calculator(int offset)
    {
        _offset = offset;
    }

    public int Add(int left, int right) => Twice(left + right) + _offset;

    private int Twice(int value) => value * 2;
}
