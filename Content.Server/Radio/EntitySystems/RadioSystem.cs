using System.Linq;
using System.Numerics;
using Content.Server._NF.Radio; // Frontier
using Content.Server.Administration.Logs;
using Content.Server.Chat.Systems;
using Content.Server.Humanoid;
using Content.Server.IdentityManagement;
using Content.Server.Power.Components;
using Content.Server.Radio.Components;
using Content.Shared._Coyote;
using Content.Shared._Coyote.RadioNoises;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Robust.Server.GameObjects; // Frontier
using Content.Shared.Speech;
using Content.Shared.Ghost;
using Content.Shared.Humanoid;
using Content.Shared.IdentityManagement;
using Content.Shared.Random.Helpers;
using Robust.Shared.Enums; // Nuclear-14
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Replays;
using Robust.Shared.Utility;

namespace Content.Server.Radio.EntitySystems;

/// <summary>
///     This system handles intrinsic radios and the general process of converting radio messages into chat messages.
/// </summary>
public sealed class RadioSystem : EntitySystem
{
    [Dependency] private readonly INetManager _netMan = default!;
    [Dependency] private readonly IReplayRecordingManager _replay = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly TransformSystem _t = default!;
    [Dependency] private readonly HumanoidAppearanceSystem _ham = default!;

    // set used to prevent radio feedback loops.
    private readonly HashSet<string> _messages = new();

    private EntityQuery<TelecomExemptComponent> _exemptQuery;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<IntrinsicRadioReceiverComponent, RadioReceiveEvent>(OnIntrinsicReceive);
        SubscribeLocalEvent<IntrinsicRadioTransmitterComponent, EntitySpokeEvent>(OnIntrinsicSpeak);

        _exemptQuery = GetEntityQuery<TelecomExemptComponent>();
    }

    private void OnIntrinsicSpeak(EntityUid uid, IntrinsicRadioTransmitterComponent component, EntitySpokeEvent args)
    {
        if (args.Channel != null && component.Channels.Contains(args.Channel.ID))
        {
            SendRadioMessage(uid, args.Message, args.Channel, uid);
            args.Channel = null; // prevent duplicate messages from other listeners.
        }
    }

    //Nuclear-14
    /// <summary>
    /// Gets the message frequency, if there is no such frequency, returns the standard channel frequency.
    /// </summary>
    public int GetFrequency(EntityUid source, RadioChannelPrototype channel)
    {
        if (TryComp<RadioMicrophoneComponent>(source, out var radioMicrophone))
            return radioMicrophone.Frequency;

        return channel.Frequency;
    }

    private void OnIntrinsicReceive(EntityUid uid, IntrinsicRadioReceiverComponent component, ref RadioReceiveEvent args)
    {
        if (!TryComp(uid, out ActorComponent? actor))
            return;

        MsgChatMessage chatMess = MangleRadioMessage(
            uid,
            ref args,
            out RadioDegradationParams dParams);

        if (dParams.DropMessageEntirely || dParams.DropMessage)
            return;
        // do the KSSHHT
        var staticEv = new DoRadioStaticEvent(
            uid,
            args.MessageSource,
            actor.PlayerSession.AttachedEntity,
            args.Channel.ID,
            args.Message,
            dParams);
        RaiseLocalEvent(uid, ref staticEv);
        // Send the message to the client
        _netMan.ServerSendMessage(chatMess, actor.PlayerSession.Channel);
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    public void SendRadioMessage(EntityUid messageSource, string message, ProtoId<RadioChannelPrototype> channel, EntityUid radioSource, int? frequency = null, bool escapeMarkup = true) // Frontier: added frequency
    {
        SendRadioMessage(
            messageSource,
            message,
            _prototype.Index(channel),
            radioSource,
            frequency: frequency,
            escapeMarkup: escapeMarkup); // Frontier: added frequency
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    /// <param name="messageSource">Entity that spoke the message</param>
    /// <param name="radioSource">Entity that picked up the message and will send it, e.g. headset</param>
    public void SendRadioMessage(
        EntityUid messageSource,
        string message,
        RadioChannelPrototype channel,
        EntityUid radioSource,
        int? frequency = null,
        bool escapeMarkup = true
    ) // Nuclear-14: add frequency
    {
        // TODO if radios ever garble / modify messages, feedback-prevention needs to be handled better than this.
        if (!_messages.Add(message))
            return;

        var evt = new TransformSpeakerNameEvent(messageSource, MetaData(messageSource).EntityName);
        RaiseLocalEvent(messageSource, evt);

        // Frontier: add name transform event
        var transformEv = new RadioTransformMessageEvent(channel, radioSource, evt.VoiceName, message, messageSource);
        RaiseLocalEvent(radioSource, ref transformEv);
        message = transformEv.Message;
        messageSource = transformEv.MessageSource;
        // End Frontier

        var name = transformEv.Name; // Frontier: evt.VoiceName<transformEv.Name
        name = FormattedMessage.EscapeText(name);

        SpeechVerbPrototype speech;
        if (evt.SpeechVerb != null && _prototype.TryIndex(evt.SpeechVerb, out var evntProto))
            speech = evntProto;
        else
            speech = _chat.GetSpeechVerb(messageSource, message);

        var content = escapeMarkup
            ? FormattedMessage.EscapeText(message)
            : message;

        // Frontier: append frequency if the channel requests it
        string channelText;
        if (channel.ShowFrequency)
            channelText = $"\\[{channel.LocalizedName} ({frequency})\\]";
        else
            channelText = $"\\[{channel.LocalizedName}\\]";
        // End Frontier

        var entityName = Identity.Name(messageSource, EntityManager);
        if (string.IsNullOrEmpty(entityName))
        {
            // If no name override is provided, we use the entity's name.
            entityName = "bingles";
        }
        var nameHashColor = ColorExtensions.ConsistentRandomSeededColorFromString(entityName);
        var nameHashColorAdjusted = ColorExtensions.PreventColorFromBeingTooCloseToTheBackgroundColor(nameHashColor); // pastilla loses
        var nameColorString = nameHashColorAdjusted.ToHex();

        var varb = Loc.GetString(_random.Pick(speech.SpeechVerbStrings));

        // the exploded radio message, so we can mess with it
        RadioMessageDataHolder radioMessageData = new(
            speech.Bold ? "chat-radio-message-wrap-bold" : "chat-radio-message-wrap",
            channel.Color,
            nameColorString,
            speech.FontId,
            speech.FontSize,
            varb,
            channelText,
            name,
            content,
            messageSource,
            channel); // yeah its long, just like me

        // the normal, crystal-clear radio message formatting
        var wrappedMessage = Loc.GetString(speech.Bold ? "chat-radio-message-wrap-bold" : "chat-radio-message-wrap",
            ("color", channel.Color),
            ("chatColor", nameColorString),
            ("fontType", speech.FontId),
            ("fontSize", speech.FontSize),
            ("verb", varb),
            ("channel", channelText), // Frontier: $"\\[{channel.LocalizedName}\\]"<channelText
            ("name", name),
            ("message", content));
        // most radios are relayed to chat, so lets parse the chat message beforehand
        var chat = new ChatMessage(
            ChatChannel.Radio,
            message,
            wrappedMessage,
            NetEntity.Invalid,
            null);
        var chatMsg = new MsgChatMessage { Message = chat };

        var ev = new RadioReceiveEvent(
            message,
            messageSource,
            channel,
            radioSource,
            chatMsg,
            radioMessageData);

        var sendAttemptEv = new RadioSendAttemptEvent(channel, radioSource);
        RaiseLocalEvent(ref sendAttemptEv);
        RaiseLocalEvent(radioSource, ref sendAttemptEv);
        var canSend = !sendAttemptEv.Cancelled;

        var sourceMapId = Transform(radioSource).MapID;
        var hasActiveServer = HasActiveServer(sourceMapId, channel.ID);
        var sourceServerExempt = _exemptQuery.HasComp(radioSource);

        var radioQuery = EntityQueryEnumerator<ActiveRadioComponent, TransformComponent>();

        if (frequency == null) // Nuclear-14
            frequency = GetFrequency(messageSource, channel); // Nuclear-14

        while (canSend && radioQuery.MoveNext(out var receiver, out var radio, out var transform))
        {
            if (!radio.ReceiveAllChannels)
            {
                if (!radio.Channels.Contains(channel.ID)
                    || (TryComp<IntercomComponent>(receiver, out var intercom)
                        && !intercom.SupportedChannels.Contains(channel.ID)))
                    continue;
            }

            if (!HasComp<GhostComponent>(receiver) && GetFrequency(receiver, channel) != frequency) // Nuclear-14
                continue; // Nuclear-14

            if (!channel.LongRange && transform.MapID != sourceMapId && !radio.GlobalReceive)
                continue;

            // Check if within range for range-limited channels
            if (channel.MaxRange.HasValue && channel.MaxRange.Value > 0)
            {
                var sourcePos = Transform(radioSource).WorldPosition;
                var targetPos = transform.WorldPosition;

                // Check distance between sender and receiver
                if ((sourcePos - targetPos).Length() > channel.MaxRange.Value)
                    continue;
            }

            // don't need telecom server for long range channels or handheld radios and intercoms
            var needServer = !channel.LongRange && !sourceServerExempt;
            if (needServer && !hasActiveServer)
                continue;

            // check if message can be sent to specific receiver
            var attemptEv = new RadioReceiveAttemptEvent(channel, radioSource, receiver);
            RaiseLocalEvent(ref attemptEv);
            RaiseLocalEvent(receiver, ref attemptEv);
            if (attemptEv.Cancelled)
                continue;

            // send the message
            RaiseLocalEvent(receiver, ref ev);
        }

        if (name != Name(messageSource))
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} as {name} on {channel.LocalizedName}: {message}");
        else
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} on {channel.LocalizedName}: {message}");

        _replay.RecordServerMessage(chat);
        _messages.Remove(message);
    }

    /// <inheritdoc cref="TelecomServerComponent"/>
    private bool HasActiveServer(MapId mapId, string channelId)
    {
        var servers = EntityQuery<TelecomServerComponent, EncryptionKeyHolderComponent, ApcPowerReceiverComponent, TransformComponent>();
        foreach (var (_, keys, power, transform) in servers)
        {
            if (transform.MapID == mapId &&
                power.Powered &&
                keys.Channels.Contains(channelId))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Mangles the message based on radio channel properties and other random shenanigans.
    /// </summary>
    public MsgChatMessage MangleRadioMessage(
            EntityUid radioReceiver,
            ref RadioReceiveEvent args,
            out RadioDegradationParams degradationParams
            )
    {
        degradationParams = new RadioDegradationParams(
            wordDropPercentage: 0,
            letterDropPercentage: 0,
            fontSizeDecrease: 0,
            generifyChannel: false,
            generifyStatic: false,
            generifyName: false,
            dropMessage: false,
            dropMessageEntirely: false);
        RadioChannelPrototype channel = args.Channel;
        if (!channel.UseRangeDegradation || args.MessageDataHolder is null)
            return args.ChatMsg;
        EntityUid? senderGridUid = _t.GetGrid(args.MessageSource);
        EntityUid? receiverGridUid = _t.GetGrid(radioReceiver);
        // same grid? they can hear u
        if (senderGridUid.HasValue
            && receiverGridUid.HasValue
            && senderGridUid == receiverGridUid)
            return args.ChatMsg;
        // get distance between sender and receiver
        Vector2 receiverPos = _t.GetWorldPosition(radioReceiver);
        Vector2 senderPos   = _t.GetWorldPosition(args.MessageSource);
        float   distance    = (receiverPos - senderPos).Length();
        float   optiRange   = channel.OptimalRange;
        // early bounce if in optimal range
        if (distance <= optiRange)
            return args.ChatMsg;
        // get the current grid's diameter if either entity is on a grid
        float gridDiameter = 0f;
        if (TryComp(receiverGridUid, out MapGridComponent? receiverGrid))
        {
            gridDiameter += Math.Max(receiverGrid.LocalAABB.Width, receiverGrid.LocalAABB.Height);
        }
        if (TryComp(senderGridUid, out MapGridComponent? senderGrid))
        {
            gridDiameter += Math.Max(senderGrid.LocalAABB.Width, senderGrid.LocalAABB.Height);
        }
        optiRange += Math.Min(gridDiameter * 0.5f, optiRange * 0.2f); // max 20% increase to optimal range from grids
        if (distance <= optiRange)
            return args.ChatMsg;
        // okay time to degrade
        RadioMessageDataHolder radioData = args.MessageDataHolder;
        // how messed up is it?
        float lightDegradDist = optiRange + channel.LightDegradationRange;
        float heavyDegradDist = lightDegradDist + channel.HeavyDegradationRange;

        // Calculate the total range where degradation occurs.
        float degradationRange = heavyDegradDist - optiRange;
        float proportionThrough = (distance - optiRange) / degradationRange;
        proportionThrough = Math.Clamp(proportionThrough, 0f, 1f);

        float charDropBase = Math.Max(channel.DegradationMaxPercent * proportionThrough, channel.DegradationMinPercent);
        float wordDropBase = charDropBase * channel.DegradationWordMult;

        // first, if it is beyond heavy degradation range, we have a chance to not hear anything at all
        if (distance > heavyDegradDist)
        {
            float missChance = channel.BeyondHeavyDegradationDropChance;
            if (_random.Prob(missChance) || distance > heavyDegradDist + channel.TotalDegradationRange)
            {
                degradationParams.DropMessage = true;
                degradationParams.GenerifyStatic = true;
                return args.ChatMsg;
            }
            // else we just degrade heavily
            {
                float maxDeg = channel.DegradationMaxPercent * channel.HeavyDegradationMultiplier;
                degradationParams.WordDropPercentage    = maxDeg;
                degradationParams.LetterDropPercentage  = maxDeg;
                degradationParams.FontSizeDecrease      = 4;
                degradationParams.GenerifyChannel       = true;
                degradationParams.GenerifyStatic        = true;
                degradationParams.GenerifyName          = true;
                degradationParams.Whisperfy             = true;
            }
        }
        // less than heavy degradation distance but more than light degradation distance
        else if (distance > lightDegradDist)
        {
            degradationParams.WordDropPercentage   = wordDropBase * channel.HeavyDegradationMultiplier;
            degradationParams.LetterDropPercentage = charDropBase * channel.HeavyDegradationMultiplier;
            degradationParams.FontSizeDecrease     = 3;
            degradationParams.GenerifyName         = true;
            degradationParams.GenerifyChannel      = true;
            degradationParams.Whisperfy            = true;
        }
        else
        {
            degradationParams.WordDropPercentage   =  wordDropBase;
            degradationParams.LetterDropPercentage =  charDropBase;
            degradationParams.FontSizeDecrease     =  2;
        }
        // Mangle the message
        string mangledMessage = MangleMessage(
            radioData.Message,
            degradationParams.WordDropPercentage,
            degradationParams.LetterDropPercentage);
        // apply font size decrease
        int newFontSize = Math.Max(1, radioData.FontSize - degradationParams.FontSizeDecrease);
        // if generifying, do so now
        string name = radioData.Name;
        if (degradationParams.GenerifyName)
        {
            name = GenerifyName(radioData);
            degradationParams.NameOverride = name;
        }
        // if generifying channel, change channel text to ???
        string channelText  = radioData.ChannelText;
        Color  channelColor = radioData.Color;
        if (degradationParams.GenerifyChannel)
        {
            channelText  = @"\[???\]";
            channelColor = Color.Gray;
            degradationParams.ColorOverride = channelColor;
        }
        // recolor name to gray if generifying static
        string nameColorString = degradationParams.GenerifyStatic ? Color.Gray.ToHex() : radioData.ChatColor;
        // load the overrides

        // the normal, crystal-clear radio message formatting
        var wrappedMessage = Loc.GetString(
            radioData.LocBase,
            ("color", channelColor),
            ("chatColor", nameColorString),
            ("fontType", radioData.FontType),
            ("fontSize", newFontSize),
            ("verb", radioData.Verb),
            ("channel", channelText), // Frontier: $"\\[{channel.LocalizedName}\\]"<channelText
            ("name", name),
            ("message", mangledMessage));
        // and since radio speakers on nonheadsets dont respect colors and sizes and whatever,
        // we'll just make a new chat message for them too, with tags baked in
        // mangledMessage = $"[color={channelColor.ToHex()}][size={newFontSize}]{mangledMessage}[/size][/color]";
        // most radios are relayed to chat, so lets parse the chat message beforehand
        var chat = new ChatMessage(
            ChatChannel.Radio,
            mangledMessage,
            wrappedMessage,
            NetEntity.Invalid,
            null);
        var chatMsg = new MsgChatMessage { Message = chat };
        return chatMsg;
    }

    /// <summary>
    /// Mangles a message by randomly dropping words and letters based on given percentages.
    /// dropped letters are replaced with cute unicode gunchery.
    /// </summary>
    private string MangleMessage(string message, float wordDropFloat, float letterDropFloat)
    {
        List<string> staticReplacements = new()
        {
            "▓","▒","░",
        };
        wordDropFloat   = Math.Clamp(wordDropFloat, 0f, 1f);
        letterDropFloat = Math.Clamp(letterDropFloat, 0f, 1f);
        string[] words = message.Split(' ');
        // instead of just a sort of roll for each, we're going to use the percentages to determine actual counts to drop
        // so if, like, we get like, 45 words in a message and a 10% drop rate, we drop 4 or 5 words guaranteed instead of rolling each word with 10%
        int wordsToDrop = (int)Math.Round(words.Length * wordDropFloat);
        wordsToDrop = Math.Clamp(wordsToDrop, 1, words.Length);
        // drop words first
        HashSet<int> wordIndices = Enumerable
            .Range(0, words.Length)
            .OrderBy(x => _random.Next())
            .Take(wordsToDrop)
            .ToHashSet();
        // and we do the same to figure how many words will have missing letters
        // pick through the remaining words and pick out letterDropFloat percent of words to drop letters from
        int letterDropWordsCount = (int)Math.Round((words.Length - wordIndices.Count) * letterDropFloat);
        HashSet<int> letterDropWords = Enumerable
            .Range(0, words.Length)
            .Where(x => !wordIndices.Contains(x))
            .OrderBy(x => _random.Next())
            .Take(letterDropWordsCount)
            .ToHashSet();

        for (int i = 0; i < words.Length; i++)
        {
            // if this word is selected for dropping
            if (wordIndices.Contains(i))
            {
                string newWord = "";
                for (int j = 0; j < words[i].Length; j++)
                {
                    newWord += _random.Pick(staticReplacements);
                }
                words[i] = newWord;
                continue;
            }
            if (letterDropWords.Contains(i))
            {
                // drop individual letters
                char[] letters = words[i].ToCharArray();
                int lettersToDrop = (int)Math.Round(letters.Length * letterDropFloat);
                lettersToDrop = Math.Clamp(
                    lettersToDrop,
                    1,
                    letters.Length);
                HashSet<int> letterIndices = Enumerable
                    .Range(0, letters.Length)
                    .OrderBy(x => _random.Next())
                    .Take(lettersToDrop)
                    .ToHashSet();
                for (int j = 0; j < letters.Length; j++)
                {
                    if (letterIndices.Contains(j))
                    {
                        letters[j] = _random.Pick(staticReplacements)[0];
                    }
                }

                words[i] = new string(letters);
            }
            else // just replace 1 letter in the word with static
            {
                char[] letters = words[i].ToCharArray();
                int letterIdx = _random.Next(0, letters.Length);
                letters[letterIdx] = _random.Pick(staticReplacements)[0];
                words[i] = new string(letters);
            }
        }
        if (wordDropFloat + letterDropFloat >= 0.5f)
        {
            for (int k = 0; k < words.Length / 4; k++)
            {
                int idx1 = _random.Next(0, words.Length);
                int idx2 = _random.Next(0, words.Length);
                // swap
                (words[idx1], words[idx2]) = (words[idx2], words[idx1]);
            }
        }
        // replace spaces with static, based on letter drop float
        string finalMessage = "";
        for (int i = 0; i < words.Length; i++)
        {
            finalMessage += words[i];
            if (i < words.Length - 1)
            {
                if (_random.Prob(letterDropFloat))
                {
                    finalMessage += _random.Pick(staticReplacements);
                }
                else
                {
                    finalMessage += " ";
                }
            }
        }

        return finalMessage;
    }

    /// <summary>
    /// Replaces the name with their generic equivalent, as set by Identity
    /// </summary>
    private string GenerifyName(RadioMessageDataHolder radioData)
    {
        EntityUid? source = radioData.Sender;
        if (source is null)
            return "someone";
        int    age     = 18;
        Gender gender  = Gender.Epicene;
        string species = SharedHumanoidAppearanceSystem.DefaultSpecies;

        // Always use their actual age and gender, since that can't really be changed by an ID.
        if (TryComp<HumanoidAppearanceComponent>(source.Value, out var appearance))
        {
            gender  = appearance.Gender;
            age     = appearance.Age;
            species = appearance.Species;
        }
        string middleaged = _ham.GetAgeRepresentation(species, age);
        string genderString = gender switch
        {
            Gender.Female                        => Loc.GetString("identity-gender-feminine"),
            Gender.Male                          => Loc.GetString("identity-gender-masculine"),
            Gender.Epicene or Gender.Neuter or _ => Loc.GetString("identity-gender-person")
        };
        return $"{middleaged} {genderString}";
        // remind me to expose Identity system's... everything
    }
}
