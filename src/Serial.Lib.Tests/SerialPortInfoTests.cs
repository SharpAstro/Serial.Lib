using Shouldly;
using Xunit;

namespace SharpAstro.Serial.Tests;

public sealed class SerialPortInfoTests
{
    [Fact]
    public void ASerialNumberMakesTheIdentityTheDevice()
    {
        var info = new SerialPortInfo("COM5") { VendorId = 0x0403, ProductId = 0x6001, SerialNumber = "A50285BI", LocationPath = "PCIROOT(0)#USB(2)" };

        info.Identity.ShouldBe(SerialIdentityKind.Device);
        info.IdentityKey.ShouldBe("usb:0403:6001:A50285BI");
    }

    [Fact]
    public void WithoutASerialNumberTheSocketIsTheIdentity()
    {
        var info = new SerialPortInfo("COM3") { VendorId = 0x1a86, ProductId = 0x7523, LocationPath = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)" };

        info.Identity.ShouldBe(SerialIdentityKind.Socket);
        info.IdentityKey.ShouldBe("socket:PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(3)");
    }

    [Fact]
    public void WithNothingElseTheNameIsTheIdentity()
    {
        var info = new SerialPortInfo("COM1");

        info.Identity.ShouldBe(SerialIdentityKind.PortName);
        info.IdentityKey.ShouldBe("name:COM1");
    }
}
