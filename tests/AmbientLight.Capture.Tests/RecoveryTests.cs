using AmbientLight.Capture.Recovery;
using SharpGen.Runtime;
using DxgiResult = Vortice.DXGI.ResultCode;

namespace AmbientLight.Capture.Tests;

public sealed class RecoveryTests
{
    [Theory]
    [InlineData(unchecked((int)0x887A0027), CaptureRecovery.None)]                        // DXGI_ERROR_WAIT_TIMEOUT
    [InlineData(unchecked((int)0x887A0026), CaptureRecovery.RecreateDuplication)]         // DXGI_ERROR_ACCESS_LOST
    [InlineData(unchecked((int)0x887A0025), CaptureRecovery.WaitThenRecreateDuplication)] // DXGI_ERROR_MODE_CHANGE_IN_PROGRESS
    [InlineData(unchecked((int)0x80070005), CaptureRecovery.WaitThenRecreateDuplication)] // E_ACCESSDENIED (secure desktop)
    [InlineData(unchecked((int)0x887A0022), CaptureRecovery.WaitThenRecreateDuplication)] // DXGI_ERROR_NOT_CURRENTLY_AVAILABLE
    [InlineData(unchecked((int)0x887A0028), CaptureRecovery.WaitThenRecreateDuplication)] // DXGI_ERROR_SESSION_DISCONNECTED
    [InlineData(unchecked((int)0x887A0005), CaptureRecovery.RecreateDevice)]              // DXGI_ERROR_DEVICE_REMOVED
    [InlineData(unchecked((int)0x887A0007), CaptureRecovery.RecreateDevice)]              // DXGI_ERROR_DEVICE_RESET
    [InlineData(unchecked((int)0x887A0006), CaptureRecovery.RecreateDevice)]              // DXGI_ERROR_DEVICE_HUNG
    [InlineData(unchecked((int)0x887A0002), CaptureRecovery.RecreateDevice)]              // DXGI_ERROR_NOT_FOUND (output unplugged)
    [InlineData(unchecked((int)0x887A0004), CaptureRecovery.Fatal)]                       // DXGI_ERROR_UNSUPPORTED
    [InlineData(unchecked((int)0x80004005), CaptureRecovery.RecreateDevice)]              // E_FAIL: unknown failure
    [InlineData(0, CaptureRecovery.None)]                                                 // S_OK
    public void Classify_MapsHResults(int hresult, CaptureRecovery expected)
    {
        Assert.Equal(expected, DxgiErrorClassifier.Classify(hresult));
    }

    [Fact]
    public void Classify_UsesVorticeConstants_ForTheSameCodes()
    {
        Assert.Equal(unchecked((int)0x887A0026), DxgiResult.AccessLost.Code);
        Assert.Equal(unchecked((int)0x887A0027), DxgiResult.WaitTimeout.Code);
        Assert.Equal(unchecked((int)0x80070005), Result.AccessDenied.Code);
    }

    [Fact]
    public void Classify_Exceptions()
    {
        Assert.Equal(CaptureRecovery.RecreateDuplication, DxgiErrorClassifier.Classify(new SharpGenException(DxgiResult.AccessLost)));
        Assert.Equal(CaptureRecovery.WaitThenRecreateDuplication, DxgiErrorClassifier.Classify(
            new CaptureException("wrapped", new SharpGenException(Result.AccessDenied))));
        Assert.Equal(CaptureRecovery.RecreateDevice, DxgiErrorClassifier.Classify(
            new CaptureException("explicit", CaptureRecovery.RecreateDevice)));
        Assert.Equal(CaptureRecovery.Fatal, DxgiErrorClassifier.Classify(new NotSupportedException("format")));
    }

    [Fact]
    public void Backoff_RetriesAccessLostImmediately_ThenDoublesUpToTheCap()
    {
        var backoff = new RecoveryBackoff();

        Assert.Equal(TimeSpan.Zero, backoff.NextDelay(CaptureRecovery.RecreateDuplication));
        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.NextDelay(CaptureRecovery.RecreateDuplication));
        Assert.Equal(TimeSpan.FromMilliseconds(200), backoff.NextDelay(CaptureRecovery.RecreateDevice));
        Assert.Equal(TimeSpan.FromMilliseconds(400), backoff.NextDelay(CaptureRecovery.RecreateDevice));

        for (var i = 0; i < 20; i++)
        {
            Assert.True(backoff.NextDelay(CaptureRecovery.WaitThenRecreateDuplication) <= RecoveryBackoff.MaximumDelay);
        }

        Assert.Equal(RecoveryBackoff.MaximumDelay, backoff.NextDelay(CaptureRecovery.WaitThenRecreateDuplication));
        Assert.Equal(25, backoff.ConsecutiveFailures);
    }

    [Fact]
    public void Backoff_FirstWaitIsShort_FatalAlwaysWaitsTheMaximum_ResetClears()
    {
        var backoff = new RecoveryBackoff();

        Assert.Equal(RecoveryBackoff.InitialDelay, backoff.NextDelay(CaptureRecovery.WaitThenRecreateDuplication));
        Assert.Equal(RecoveryBackoff.MaximumDelay, backoff.NextDelay(CaptureRecovery.Fatal));

        backoff.Reset();

        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.Equal(TimeSpan.Zero, backoff.NextDelay(CaptureRecovery.RecreateDuplication));
    }
}
