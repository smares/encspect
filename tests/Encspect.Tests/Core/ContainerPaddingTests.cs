using Encspect.Core;

namespace Encspect.Tests.Core;

public class ContainerPaddingTests
{
    // An end trimming an MP4 edit list states is not a Matroska element: the finding names the edit list and the packets the analyzer holds back.
    [Fact]
    public void The_message_for_an_edit_list_names_no_matroska_element()
    {
        string message = ContainerPadding.LargeEditMessage(9_600, 5_000, 256);

        Assert.Contains("edit list", message, StringComparison.Ordinal);
        Assert.Contains("256 packets", message, StringComparison.Ordinal);
        Assert.DoesNotContain("DiscardPadding", message, StringComparison.Ordinal);
        Assert.DoesNotContain("RFC 9559", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_for_a_Matroska_block_names_its_element_and_section()
    {
        string message = ContainerPadding.LargeMessage(9_600, 5_000, 1);

        Assert.Contains("DiscardPadding", message, StringComparison.Ordinal);
        Assert.Contains("RFC 9559, section 5.1.3.5.7", message, StringComparison.Ordinal);
    }
}
