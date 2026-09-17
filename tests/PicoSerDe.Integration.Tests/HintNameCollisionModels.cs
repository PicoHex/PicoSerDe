using PicoSerDe.Core;

namespace CollisionNs.Sub
{
    [PicoSerializable]
    public class Inner
    {
        public int V { get; set; }
    }
}

namespace CollisionNs
{
    [PicoSerializable]
    public class Sub_Inner
    {
        public int W { get; set; }
    }

    // Both colliding types appear as nested members: the generated inner-helper
    // class names for them must also be unique.
    [PicoSerializable]
    public class SubHolder
    {
        public Sub.Inner? A { get; set; }
        public Sub_Inner? B { get; set; }
    }
}
