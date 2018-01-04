using System.Collections;
using System.Collections.Generic;
using Rubberduck.VBEditor.SafeComWrappers.Abstract;
using VB = Microsoft.Vbe.Interop;

namespace Rubberduck.VBEditor.SafeComWrappers.VBA
{
    public class AddIns : SafeComWrapper<VB.Addins>, IAddIns
    {
        public AddIns(VB.Addins target, bool rewrapping = false) : 
            base(target, rewrapping)
        {
        }

        public int Count => IsWrappingNullReference ? 0 : Target.Count;

        public object Parent // todo: verify if this could be 'public Application Parent' instead
            => IsWrappingNullReference ? null : Target.Parent;

        public IVBE VBE => new VBE(IsWrappingNullReference ? null : Target.VBE);

        public IAddIn this[object index] => new AddIn(IsWrappingNullReference ? null : Target.Item(index));

        public void Update()
        {
            Target.Update();
        }

        //public override void Release(bool final = false)
        //{
        //    if (!IsWrappingNullReference)
        //    {
        //        for (var i = 1; i <= Count; i++)
        //        {
        //            this[i].Release();
        //        }
        //        base.Release(final);
        //    }
        //}

        public override bool Equals(ISafeComWrapper<VB.Addins> other)
        {
            return IsEqualIfNull(other) || (other != null && ReferenceEquals(other.Target.Parent, Parent));
        }

        public bool Equals(IAddIns other)
        {
            return Equals(other as ISafeComWrapper<VB.Addins>);
        }

        public override int GetHashCode()
        {
            return IsWrappingNullReference ? 0 : HashCode.Compute(Parent);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return IsWrappingNullReference ? new List<IEnumerable>().GetEnumerator() : Target.GetEnumerator();
        }

        IEnumerator<IAddIn> IEnumerable<IAddIn>.GetEnumerator()
        {
            return IsWrappingNullReference
                ? new ComWrapperEnumerator<IAddIn>(null, o => new AddIn(null))
                : new ComWrapperEnumerator<IAddIn>(Target, o => new AddIn((VB.AddIn) o));
        }
    }
}