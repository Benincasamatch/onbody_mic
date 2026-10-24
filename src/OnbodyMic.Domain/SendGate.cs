namespace OnbodyMic.Domain;

/// Single choke point for every frame that would leave through the virtual device.
public sealed class SendGate
{
    public SendDecision Decide(SourceTicket ticket, SendGateSnapshot snapshot)
    {
        if (snapshot.EmergencyLatched)
        {
            return SendDecision.Deny(SendDenialReason.EmergencyLatched);
        }

        if (ticket.SessionEpoch != snapshot.SessionEpoch)
        {
            return SendDecision.Deny(SendDenialReason.StaleSessionEpoch);
        }

        return ticket.Kind switch
        {
            AudioKind.Microphone => DecideMicrophone(snapshot),
            AudioKind.Application => DecideApplication(snapshot),
            AudioKind.Clip => DecideClip(ticket, snapshot),
            _ => SendDecision.Deny(SendDenialReason.SourceNotAuthorized),
        };
    }

    private static SendDecision DecideMicrophone(SendGateSnapshot snapshot) =>
        snapshot.Switches.MicSendEnabled
            ? SendDecision.Allow
            : SendDecision.Deny(SendDenialReason.MicSendDisabled);

    private static SendDecision DecideApplication(SendGateSnapshot snapshot)
    {
        if (!snapshot.Switches.ExtrasSendEnabled)
        {
            return SendDecision.Deny(SendDenialReason.ExtrasSendDisabled);
        }

        return snapshot.Switches.AppSendEnabled
            ? SendDecision.Allow
            : SendDecision.Deny(SendDenialReason.AppSendDisabled);
    }

    private static SendDecision DecideClip(SourceTicket ticket, SendGateSnapshot snapshot)
    {
        if (!snapshot.Switches.ExtrasSendEnabled)
        {
            return SendDecision.Deny(SendDenialReason.ExtrasSendDisabled);
        }

        if (snapshot.Clip is not { } clip || clip.Mode != ClipPlaybackMode.Send)
        {
            return SendDecision.Deny(SendDenialReason.ClipNotSending);
        }

        return ticket.SourceEpoch == clip.PlaybackEpoch
            ? SendDecision.Allow
            : SendDecision.Deny(SendDenialReason.StalePlaybackEpoch);
    }
}
