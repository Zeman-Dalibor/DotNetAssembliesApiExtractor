namespace TestAssemblies
{
    public interface ITestInterface
    {
        void InterfaceMethod();
    }

    public struct TestStruct
    {
        public int PublicField;
        private int _privateField;
        public int PublicProperty { get; set; }
        private int PrivateProperty { get; set; }
        public void PublicMethod() { }
        private void PrivateMethod() { }
    }

    public class PublicClass : ITestInterface
    {
        public int PublicField;
        private int _privateField;
        public int PublicProperty { get; set; }
        private int PrivateProperty { get; set; }
        public void PublicMethod() { }
        private void PrivateMethod() { }
        private class PrivateNestedClass { public int NestedField; }
        void ITestInterface.InterfaceMethod() { }
    }

    internal class InternalClass
    {
        public int PublicField;
        private int _privateField;
        public int PublicProperty { get; set; }
        private int PrivateProperty { get; set; }
        public void PublicMethod() { }
        private void PrivateMethod() { }
    }
}
