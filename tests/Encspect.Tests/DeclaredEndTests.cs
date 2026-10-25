using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The host decides from IFormatParser.HasDeclaredEnd whether to look for a container's end (reflection on the interface map is not
// available under NativeAOT), so the flag must agree with the parsers that implement DeclaredEnd.
public class DeclaredEndFlagTests
{
    [Fact]
    public void EveryFormatParserThatImplementsDeclaredEndSaysSo()
    {
        foreach (IFormatParser parser in DefaultParsers.Formats)
        {
            var map = parser.GetType().GetInterfaceMap(typeof(IFormatParser));
            int index = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IFormatParser.DeclaredEnd));
            bool implements = map.TargetMethods[index].DeclaringType != typeof(IFormatParser);
            Assert.True(implements == parser.HasDeclaredEnd, $"{parser.Name}: DeclaredEnd implemented {implements}, HasDeclaredEnd {parser.HasDeclaredEnd}");
        }
    }
}
