using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using PhinixClient.GUI;
using RimWorld;
using Trading;
using UnityEngine;
using Utils;
using Verse;
using static PhinixClient.GUI.GUIUtils;

namespace PhinixClient
{
    public class TradeWindow : Window
    {
        private const float SCROLLBAR_WIDTH = 16f;

        /// <summary>Size of the info button on pawn rows</summary>
        private const float INFO_BUTTON_SIZE = 24f;

        /// <summary>Colour for offered pawns with parts our game doesn't have (see <see cref="PawnCheck"/>)</summary>
        public static readonly Color MISSING_PARTS_COLOR = new Color(1f, 0.65f, 0.3f);

        private const float DEFAULT_SPACING = 10f;

        private const float OFFER_WINDOW_WIDTH = 400f;
        private const float OFFER_WINDOW_TITLE_HEIGHT = 20f;
        private const float OFFER_WINDOW_ROW_HEIGHT = 30f;
        private const float OFFER_WINDOW_CHECKBOX_HEIGHT = 25f;

        private const float SORT_HEIGHT = 30f;

        private const float SEARCH_TEXT_FIELD_WIDTH = 135f;

        private const float TRADE_BUTTON_HEIGHT = 30f;

        private const float TITLE_HEIGHT = 30f;

        private const float MOD_WARNING_HEIGHT = 24f;
        private const float MOD_LIST_ROW_HEIGHT = 22f;

        public override Vector2 InitialSize => new Vector2(1000f, 750f);

        private readonly Regex itemCountInputRegex = new Regex("\\d*");
        private readonly Texture2D tradeArrows = ContentFinder<Texture2D>.Get("tradeArrows");

        private Vector2 ourOfferScrollPos = Vector2.zero;
        private Vector2 theirOfferScrollPos = Vector2.zero;
        private Vector2 availableItemsScrollPos = Vector2.zero;

        private List<StackedThings> ourOfferCache = new List<StackedThings>();
        private List<StackedThings> theirOfferCache = new List<StackedThings>();

        /// <summary>
        /// Mods their offer needs that we don't have, and mods ours needs that they don't. See <see cref="ModCheck"/>.
        /// </summary>
        private List<ModCheck.MissingMod> theirMissingMods = new List<ModCheck.MissingMod>();
        private List<ModCheck.MissingMod> ourMissingMods = new List<ModCheck.MissingMod>();

        /// <summary>
        /// The other player's mod list <see cref="ourMissingMods"/> was worked out from, so it's redone when the list
        /// arrives or changes. Null until the server tells us.
        /// </summary>
        private HashSet<string> ourMissingModsFor;

        /// <summary>
        /// Whether each offer's missing mods list is open instead of its items, and which mods in it are expanded
        /// (by "ours:" or "theirs:" plus the package ID).
        /// </summary>
        private bool ourModWarningOpen;
        private bool theirModWarningOpen;
        private HashSet<string> expandedMods = new HashSet<string>();
        private Vector2 ourModWarningScrollPos = Vector2.zero;
        private Vector2 theirModWarningScrollPos = Vector2.zero;

        /// <summary>
        /// The trade this window contains.
        /// Will be overwritten with <see cref="updatedTrade"/> by the UI thread if <see cref="tradeUpdated"/> is set.
        /// </summary>
        private ImmutableTrade trade;
        /// <summary>
        /// Updated copy of <see cref="trade"/>.
        /// </summary>
        private ImmutableTrade updatedTrade;
        /// <summary>
        /// Whether <see cref="updatedTrade"/> has been changed and should be copied into <see cref="trade"/> by the UI thread.
        /// </summary>
        private bool tradeUpdated = false;
        /// <summary>
        /// Lock object protecting <see cref="updatedTrade"/>.
        /// </summary>
        private object updatedTradeLock = new object();

        /// <summary>
        /// All items that can be added to the trade.
        /// </summary>
        private List<StackedThings> availableItems = new List<StackedThings>();
        /// <summary>
        /// Items that can be added to the trade filtered by <see cref="searchText"/>.
        /// </summary>
        private List<StackedThings> filteredAvailableItems = new List<StackedThings>();
        /// <summary>
        /// Text to filter items that can be added to the trade.
        /// </summary>
        /// <seealso cref="availableItems"/>
        /// <seealso cref="filteredAvailableItems"/>
        private string searchText = string.Empty;

        /// <summary>
        /// Token of our last offer change, so we don't send another while the server hasn't answered this one.
        /// </summary>
        /// <remarks>
        /// Each update sends our whole offer. If we sent a second one before the first was answered, it would be built
        /// from an offer that doesn't include the first one's items yet, and those items would silently drop off.
        /// </remarks>
        private string waitingToken;

        /// <summary>
        /// Creates a new <see cref="TradeWindow"/> for the given trade ID.
        /// </summary>
        /// <param name="trade">Trade details</param>
        public TradeWindow(ImmutableTrade trade)
        {
            this.trade = trade;

            // Opening a trade counts as seeing it, so it drops out of the Trades tab's "New" count
            Client.Instance.MarkTradeSeen(trade.TradeId);

            this.doCloseX = true;
            this.closeOnAccept = false;
            this.closeOnCancel = false;
            this.closeOnClickedOutside = false;
            this.forcePause = true;
            this.draggable = true;
        }

        public override void PreOpen()
        {
            base.PreOpen();

            // Subscribe to events
            Client.Instance.OnTradeCompleted += OnTradeFinished;
            Client.Instance.OnTradeCancelled += OnTradeFinished;
            Client.Instance.OnTradeUpdateSuccess += OnTradeUpdated;
            Client.Instance.OnTradeUpdateFailure += OnTradeUpdated;

            // Select things from all maps that are player homes
            IEnumerable<Map> homeMaps = Find.Maps.Where(map => map.IsPlayerHome);
            IEnumerable<Thing> things;
            if (Client.Instance.Settings.AllItemsTradable)
            {
                // Get *everything*
                things = homeMaps.SelectMany(map => map.listerThings.AllThings);
            }
            else
            {
                // From each map, select all haul destinations, then everything stored there
                IEnumerable<SlotGroup> haulDestinations = homeMaps.SelectMany(map => map.haulDestinationManager.AllGroups);
                things = haulDestinations.SelectMany(haulDestination => haulDestination.HeldThings);
            }

            // Only items go in this list; pawns are added separately below. Biocoded items can only go with the colonist
            // they're coded to
            things = things.Where(thing => thing.def.category == ThingCategory.Item && !thing.def.IsCorpse && ColonyItems.CanLeaveOnItsOwn(thing));

            // Add every tame animal on our home maps, skipping dead ones
            // Animals can't stack with each other, so GroupThings gives each one its own row
            IEnumerable<Thing> animals = homeMaps
                .SelectMany(map => map.mapPawns.SpawnedColonyAnimals)
                .Where(animal => !animal.Dead);
            things = things.Concat(animals);

            // Add every free colonist on our home maps (see ColonistConverter.CanTrade for who counts)
            IEnumerable<Thing> colonists = homeMaps
                .SelectMany(map => map.mapPawns.FreeColonistsSpawned)
                .Where(ColonistConverter.CanTrade);
            things = things.Concat(colonists);

            // Group all items and cache them for later
            availableItems = StackedThings.GroupThings(things);
            filteredAvailableItems = availableItems;

            // Pre-fill offer caches as well
            ourOfferCache = buildOfferCache(trade.ItemsOnOffer);
            theirOfferCache = buildOfferCache(trade.OtherPartyItemsOnOffer);
            refreshMissingMods();
        }

        /// <summary>
        /// Works out both offers' missing mods again. Ours needs the other player's mod list, which arrives a moment
        /// after the window opens; until then (or on a server that can't tell us) that warning stays empty.
        /// </summary>
        private void refreshMissingMods()
        {
            theirMissingMods = ModCheck.MissingFromTheirOffer(trade.OtherPartyItemsOnOffer);

            Client.Instance.TryGetModList(trade.OtherPartyUuid, out ourMissingModsFor);
            ourMissingMods = ourMissingModsFor != null ? ModCheck.MissingForThem(trade.ItemsOnOffer, ourMissingModsFor) : new List<ModCheck.MissingMod>();
        }

        public override void Close(bool doCloseSound = true)
        {
            base.Close(doCloseSound);

            // Unsubscribe from events
            Client.Instance.OnTradeCompleted -= OnTradeFinished;
            Client.Instance.OnTradeCancelled -= OnTradeFinished;
        }

        public override void DoWindowContents(Rect inRect)
        {
            // Update trade if requested
            if (tradeUpdated)
            {
                if (Monitor.TryEnter(updatedTradeLock))
                {
                    // Copy the new trade details into place
                    trade = updatedTrade;

                    // Refresh trade caches
                    ourOfferCache = buildOfferCache(trade.ItemsOnOffer);
                    theirOfferCache = buildOfferCache(trade.OtherPartyItemsOnOffer);
                    refreshMissingMods();

                    // Reset the update flag
                    tradeUpdated = false;

                    Monitor.Exit(updatedTradeLock);
                }
            }

            // Their mod list arrives (or changes) after the window opens
            if (Client.Instance.TryGetModList(trade.OtherPartyUuid, out HashSet<string> theirMods) && theirMods != ourMissingModsFor) refreshMissingMods();

            // Build layout rects
            Rect titleRect = inRect.TopPartPixels(TITLE_HEIGHT);

            Rect offerHalfRect = new Rect(inRect.xMin, titleRect.yMax + DEFAULT_SPACING, inRect.width, (inRect.height - titleRect.height - DEFAULT_SPACING) / 2 - DEFAULT_SPACING / 2);
            Rect ourOfferRect = offerHalfRect.LeftPartPixels(OFFER_WINDOW_WIDTH);
            Rect theirOfferRect = offerHalfRect.RightPartPixels(OFFER_WINDOW_WIDTH);

            Rect centreColumnRect = new Rect(ourOfferRect.xMax + DEFAULT_SPACING, offerHalfRect.yMin, (theirOfferRect.xMin - DEFAULT_SPACING) - (ourOfferRect.xMax + DEFAULT_SPACING), offerHalfRect.height);
            Rect cancelButtonRect = centreColumnRect.BottomPartPixels(TRADE_BUTTON_HEIGHT);
            Rect resetButtonRect = new Rect(centreColumnRect.xMin, cancelButtonRect.yMin - (TRADE_BUTTON_HEIGHT + DEFAULT_SPACING * 2), centreColumnRect.width, TRADE_BUTTON_HEIGHT);
            Rect updateButtonRect = new Rect(centreColumnRect.xMin, resetButtonRect.yMin - (TRADE_BUTTON_HEIGHT + DEFAULT_SPACING), centreColumnRect.width, TRADE_BUTTON_HEIGHT);
            Rect tradeArrowsRect = centreColumnRect.TopPartPixels(centreColumnRect.height - (cancelButtonRect.yMax - updateButtonRect.yMin) - DEFAULT_SPACING);

            Rect searchFieldRect = new Rect(inRect.xMax - SEARCH_TEXT_FIELD_WIDTH, offerHalfRect.yMax + DEFAULT_SPACING, SEARCH_TEXT_FIELD_WIDTH, SORT_HEIGHT);
            Rect searchLabelRect = searchFieldRect.TranslatedBy(-(SEARCH_TEXT_FIELD_WIDTH + DEFAULT_SPACING));
            Rect availableItemsRect = new Rect(inRect.xMin, searchFieldRect.yMax + DEFAULT_SPACING, inRect.width, inRect.yMax - searchFieldRect.yMax - DEFAULT_SPACING);

            // Save the current text settings
            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;

            // Title
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.LabelFit(titleRect, "Phinix_trade_tradeTitle".Translate(TextHelper.StripRichText(trade.OtherPartyDisplayName)));

            // Restore the text settings
            Text.Font = previousFont;
            Text.Anchor = previousAnchor;

            // Trade arrows
            Widgets.DrawTextureFitted(tradeArrowsRect, tradeArrows, 1f);

            // Our offer
            bool ourOfferAccepted = trade.Accepted;
            drawOffer(inRect: ourOfferRect,
                title: "Phinix_trade_ourOfferLabel".Translate(),
                itemStacks: ourOfferCache,
                scrollPos: ref ourOfferScrollPos,
                accepted: ref ourOfferAccepted,
                acceptedLabel: ("Phinix_trade_confirmOurTradeCheckbox" + (trade.Accepted ? "Checked" : "Unchecked")).Translate(),
                checkboxInteractive: true,
                missingMods: ourMissingMods,
                modWarning: modWarningText(ourMissingMods, "Phinix_modCheck_theyreMissing", TextHelper.StripRichText(trade.OtherPartyDisplayName)),
                modWarningOpen: ref ourModWarningOpen,
                modWarningScrollPos: ref ourModWarningScrollPos,
                side: "ours:"
            );
            if (ourOfferAccepted != trade.Accepted)
            {
                // If they're offering animals or colonists with parts our game doesn't have (a modded bionic, a gene, an
                // item in their pockets), or items from mods we don't have, say what will be left behind before
                // agreeing. Un-accepting never needs asking
                string missing = ourOfferAccepted ? describeLeftBehind() : null;
                string tradeId = trade.TradeId;
                bool accept = ourOfferAccepted;
                if (missing == null)
                {
                    // Update our accepted state
                    new Thread(() => Client.Instance.UpdateTradeStatus(tradeId, accepted: accept)).Start();
                }
                else
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        missing,
                        () => new Thread(() => Client.Instance.UpdateTradeStatus(tradeId, accepted: true)).Start(),
                        destructive: true));
                }
            }

            // Their offer
            bool theirOfferAccepted = trade.OtherPartyAccepted;
            drawOffer(inRect: theirOfferRect,
                title: "Phinix_trade_theirOfferLabel".Translate(),
                itemStacks: theirOfferCache,
                scrollPos: ref theirOfferScrollPos,
                accepted: ref theirOfferAccepted,
                acceptedLabel: ("Phinix_trade_confirmTheirTradeCheckbox" + (trade.OtherPartyAccepted ? "Checked" : "Unchecked")).Translate(TextHelper.StripRichText(trade.OtherPartyDisplayName)),
                checkboxInteractive: false,
                missingMods: theirMissingMods,
                modWarning: modWarningText(theirMissingMods, "Phinix_modCheck_youreMissing", TextHelper.StripRichText(trade.OtherPartyDisplayName)),
                modWarningOpen: ref theirModWarningOpen,
                modWarningScrollPos: ref theirModWarningScrollPos,
                side: "theirs:"
            );

            // Update button
            if (Widgets.ButtonText(updateButtonRect, "Phinix_trade_updateButton".Translate()))
            {
                if (ItemEscrow.IsHolding(waitingToken))
                {
                    Messages.Message("Phinix_trade_waitingForServer".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                }
                else if (!selectionStillHere())
                {
                    // Something picked was eaten, used, moved or merged since the list was made. Sending it anyway
                    // would duplicate it, so the amounts are trimmed to what's there and the player checks again
                    Messages.Message("Phinix_trade_selectionChanged".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                }
                else if (Client.Instance.TradeStripsDetails(trade.TradeId) && strippedDetailsWarning() is string warning)
                {
                    // On a server running the original Phinix, some things would arrive changed. Say which first
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(warning, offerSelected, destructive: true));
                }
                else
                {
                    offerSelected();
                }
            }

            // Reset button
            if (Widgets.ButtonText(resetButtonRect, "Phinix_trade_resetButton".Translate()))
            {
                if (ItemEscrow.IsHolding(waitingToken))
                {
                    Messages.Message("Phinix_trade_waitingForServer".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                }
                else
                {
                    // Our items only come back once the server confirms it has taken them off the offer. Before, they
                    // were dropped straight away, so an empty offer that never reached the server meant they were
                    // both on offer and back home (or, the other way round, gone from both)
                    List<Thing> comingBack = trade.ItemsOnOffer.Select(TradingThingConverter.ConvertReceivedThing).ToList();
                    waitingToken = ItemEscrow.HoldReturning(comingBack, Client.Instance.TradeEscrowModule(trade.TradeId));

                    // Reset all selected counts to zero
                    foreach (StackedThings stack in availableItems)
                    {
                        stack.Selected = 0;
                    }

                    // Update trade items
                    Client.Instance.UpdateTradeItems(trade.TradeId, Array.Empty<ProtoThing>(), waitingToken);
                }
            }

            // Save GUI colour
            Color previousColour = UnityEngine.GUI.color;

            // Cancel button
            UnityEngine.GUI.color = Color.red;
            if (Widgets.ButtonText(cancelButtonRect, "Phinix_trade_cancelButton".Translate()))
            {
                new Thread(() => Client.Instance.CancelTrade(trade.TradeId)).Start();
            }

            // Restore GUI colour
            UnityEngine.GUI.color = previousColour;

            // Search label
            GUIUtils.SaveTextFormat();
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(searchLabelRect, "Phinix_trade_searchLabel".Translate());
            GUIUtils.RestoreTextFormat();

            // Search field
            string oldSearchText = searchText;
            searchText = Widgets.TextField(searchFieldRect, searchText);
            if (searchText != oldSearchText)
            {
                // Repopulate filtered item list with the new search if necessary
                filteredAvailableItems = availableItems.Where(stack => stack.Count > 0 && stack.Label.IndexOf(searchText, StringComparison.InvariantCultureIgnoreCase) > -1).ToList();
            }

            // Available items
            if (!filteredAvailableItems.Any(stack => stack.Count > 0))
            {
                // Draw a placeholder when nothing is present
                Widgets.DrawMenuSection(availableItemsRect);
                Widgets.NoneLabelCenteredVertically(availableItemsRect, ("Phinix_trade_noItemsAvailable" + (availableItems.Any() ? "WithSearch" : "")).Translate());
            }
            else
            {
                drawItemStackList(availableItemsRect, filteredAvailableItems, ref availableItemsScrollPos, true);
            }
        }

        /// <summary>
        /// Takes the picked things off the map, holds them, and sends the new offer to the server.
        /// </summary>
        private void offerSelected()
        {
            // The picks can change while a confirmation is up
            if (ItemEscrow.IsHolding(waitingToken) || !selectionStillHere()) return;

            try
            {
                List <Thing> selectedThings = new List<Thing>();
                List<Map> selectedThingMaps = new List<Map>();
                List<IntVec3> selectedThingPositions = new List<IntVec3>();

                // Collect all our things and despawn them all
                foreach (StackedThings itemStack in availableItems)
                {
                    // Pop the selected things from the stack, remembering where each was
                    // (despawning clears thing.Map, and we need it to put things back if the server says no)
                    Thing[] things = itemStack.PopSelected(selectedThingPositions, selectedThingMaps).ToArray();
                    foreach (Thing thing in things)
                    {
                        if (thing.Spawned) thing.DeSpawn();
                    }

                    // Add them to the selected things list
                    selectedThings.AddRange(things);
                }

                // Hold them under a token. They're kept in the save file until the server answers (or, if the
                // connection drops first, until the check after logging back in), so they can't just vanish.
                // Client.settleTradeOffer handles the answer
                string token = ItemEscrow.Hold(selectedThings, selectedThingMaps, Client.Instance.TradeEscrowModule(trade.TradeId), selectedThingPositions);
                waitingToken = token;


                // Get the items we have on offer and splice in the selected items
                IEnumerable<ProtoThing> actualOffer = trade.ItemsOnOffer.Concat(selectedThings.Select(TradingThingConverter.ConvertThingForSending));

                // Send an update to the server
                Client.Instance.UpdateTradeItems(trade.TradeId, actualOffer, token);
                Log.Message("Sent update");
            }
            catch (Exception e)
            {
                Log.Message(e.ToString());
            }
        }

        /// <summary>
        /// Lists the picked things a server running the original Phinix would change on the way, with how, or null if
        /// there are none. That server only passes on an item's kind, material, quality, hit points and count.
        /// </summary>
        private string strippedDetailsWarning()
        {
            List<string> lines = new List<string>();
            foreach (StackedThings stack in availableItems.Where(stack => stack.Selected > 0 && stack.Things.Count > 0))
            {
                Thing thing = stack.Things[0];
                string how = null;
                if (thing is GeneSetHolderBase) how = "Phinix_trade_stripped_genes".Translate();
                else if (thing is Pawn) how = "Phinix_trade_stripped_pawn".Translate();
                else if (thing.TryGetComp<CompArt>()?.Active == true) how = "Phinix_trade_stripped_art".Translate();
                else if (thing is Book) how = "Phinix_trade_stripped_book".Translate();
                else if (thing is ThingWithComps withComps && withComps.AllComps.Any(comp => comp.GetType().Name.IndexOf("Unique", StringComparison.OrdinalIgnoreCase) >= 0)) how = "Phinix_trade_stripped_unique".Translate();
                if (how != null) lines.Add("  - " + thing.LabelCapNoCount + ": " + how);
            }
            if (lines.Count == 0) return null;

            return "Phinix_trade_strippedWarning".Translate(TextHelper.StripRichText(trade.OtherPartyDisplayName), string.Join("\n", lines));
        }

        /// <summary>
        /// Drops anything from the picked rows that isn't on the map any more (eaten, used, carried off, merged into
        /// another stack, or a pawn that died or left), and trims each picked amount to what's left.
        /// </summary>
        /// <returns>Whether everything picked is still there</returns>
        private bool selectionStillHere()
        {
            bool allHere = true;
            foreach (StackedThings stack in availableItems.Where(stack => stack.Selected > 0))
            {
                stack.Things.RemoveAll(thing => !thing.Spawned || thing.Destroyed || (thing is Pawn pawn && pawn.Dead) || !ColonyItems.CanLeaveOnItsOwn(thing));
                if (stack.Count < stack.Selected)
                {
                    stack.Selected = stack.Count;
                    allHere = false;
                }
            }

            // Rows with nothing left disappear from the list
            filteredAvailableItems = filteredAvailableItems.Where(stack => stack.Count > 0).ToList();
            return allHere;
        }

        /// <summary>
        /// Turns the things on offer into rows for the offer lists.
        /// Items are rebuilt and stacked as before. Animals get a display-only row each, so we don't generate a whole
        /// new pawn every time the trade updates.
        /// </summary>
        /// <param name="protoThings">Things on offer</param>
        /// <returns>Rows to draw</returns>
        private static List<StackedThings> buildOfferCache(IEnumerable<ProtoThing> protoThings)
        {
            // ToList() runs the query once so we don't loop over protoThings twice
            List<ProtoThing> offer = protoThings.ToList();

            // Like Python's [t for t in offer if not is_display_pawn(t)]
            List<StackedThings> rows = StackedThings.GroupThings(offer
                .Where(protoThing => !StackedThings.IsDisplayPawn(protoThing))
                .Select(TradingThingConverter.ConvertThingFromProtoOrUnknown));

            // Animals and colonists get a display-only row each
            rows.AddRange(offer
                .Where(StackedThings.IsDisplayPawn)
                .Select(StackedThings.ForDisplayPawn));

            return rows;
        }

        /// <summary>
        /// Event handler for the <see cref="Client.OnTradeCompleted"/> and <see cref="Client.OnTradeCancelled"/> events.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void OnTradeFinished(object sender, CompleteTradeEventArgs args)
        {
            Close();
        }

        /// <summary>
        /// Event handler for the <see cref="Client.OnTradeUpdateSuccess"/> and <see cref="Client.OnTradeUpdateFailure"/> events.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void OnTradeUpdated(object sender, UITradeUpdateEventArgs args)
        {
            // Save the updated trade and flag the current one to be replaced
            lock (updatedTradeLock)
            {
                updatedTrade = args.Trade;
                tradeUpdated = true;
            }

            // Holding or letting go of the items themselves is done by Client.settleTradeOffer, which runs even if
            // this window has been closed
        }

        /// <summary>
        /// Draws an offer containing a title, list of items on offer, and toggle-able checkbox with whether it's been accepted.
        /// </summary>
        /// <param name="inRect">Container to draw within</param>
        /// <param name="title">Title text</param>
        /// <param name="itemStacks">Item stacks on offer</param>
        /// <param name="scrollPos">Item list scroll position</param>
        /// <param name="accepted">Offer accepted state</param>
        /// <param name="acceptedLabel">Accepted state label</param>
        /// <param name="checkboxInteractive">Whether to draw the accepted state checkbox in a more distinctly interactive style</param>
        /// <param name="missingMods">Mods this offer needs that one side doesn't have, shown as a warning under the title</param>
        /// <param name="modWarning">The warning's text</param>
        /// <param name="modWarningOpen">Whether the list of missing mods is showing instead of the items</param>
        /// <param name="modWarningScrollPos">Scroll position of the missing mods list</param>
        /// <param name="side">"ours:" or "theirs:", to keep each offer's expanded mods apart in <see cref="expandedMods"/></param>
        private void drawOffer(Rect inRect, string title, List<StackedThings> itemStacks, ref Vector2 scrollPos, ref bool accepted, string acceptedLabel, bool checkboxInteractive,
            List<ModCheck.MissingMod> missingMods, string modWarning, ref bool modWarningOpen, ref Vector2 modWarningScrollPos, string side)
        {
            Rect titleRect = inRect.TopPartPixels(OFFER_WINDOW_TITLE_HEIGHT);
            Rect acceptedStateRect = new Rect(inRect.xMin, inRect.yMax - OFFER_WINDOW_CHECKBOX_HEIGHT - 2.5f, inRect.width, OFFER_WINDOW_CHECKBOX_HEIGHT);
            Rect acceptedStateLabelRect = acceptedStateRect.LeftPartPixels(acceptedStateRect.width - OFFER_WINDOW_CHECKBOX_HEIGHT);
            Rect checkboxRect = new Rect(acceptedStateRect.xMax - OFFER_WINDOW_CHECKBOX_HEIGHT, acceptedStateRect.yMin + ((acceptedStateRect.height - OFFER_WINDOW_CHECKBOX_HEIGHT) / 2), OFFER_WINDOW_CHECKBOX_HEIGHT, OFFER_WINDOW_CHECKBOX_HEIGHT);
            Rect itemListRect = new Rect(inRect.xMin, titleRect.yMax, inRect.width, acceptedStateRect.yMin - DEFAULT_SPACING - titleRect.yMax);

            // Save the current text settings
            SaveTextFormat();

            // Title
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.LabelFit(titleRect, title);

            // Restore the text settings
            RestoreTextFormat();

            // Accepted state
            // Show a work tab-style checkbox texture if interactive
            if (checkboxInteractive)
            {
                SaveTextFormat();
                Text.Anchor = TextAnchor.MiddleLeft;

                Widgets.LabelFit(acceptedStateLabelRect, acceptedLabel);
                Widgets.DrawOptionBackground(checkboxRect, false);
                if (accepted) UnityEngine.GUI.DrawTexture(checkboxRect, WidgetsWork.WorkBoxCheckTex);
                if (Widgets.ButtonInvisible(acceptedStateRect, true)) accepted = !accepted;

                RestoreTextFormat();
            }
            else
            {
                Widgets.CheckboxLabeled(acceptedStateRect, acceptedLabel, ref accepted);
            }

            // A warning under the title when mods are missing. Clicking it swaps the items for the list of missing mods
            if (missingMods.Count > 0)
            {
                Rect warningRect = new Rect(itemListRect.x, itemListRect.y, itemListRect.width, MOD_WARNING_HEIGHT);
                itemListRect.yMin = warningRect.yMax + 4f;
                if (drawModWarning(warningRect, modWarning, modWarningOpen)) modWarningOpen = !modWarningOpen;
            }
            else
            {
                modWarningOpen = false;
            }

            // Items on offer, or the missing mods
            if (modWarningOpen) drawMissingModList(itemListRect, missingMods, ref modWarningScrollPos, side);
            else drawItemStackList(itemListRect, itemStacks, ref scrollPos, false);
        }

        /// <summary>
        /// The warning above an offer, like "Bob is missing 2 mods used in this offer", or null if nothing is missing.
        /// </summary>
        /// <param name="missingMods">Missing mods</param>
        /// <param name="keyPrefix">Translation key, completed with "One" or "Many"</param>
        /// <param name="otherName">The other player's name</param>
        private static string modWarningText(List<ModCheck.MissingMod> missingMods, string keyPrefix, string otherName)
        {
            if (missingMods.Count == 0) return null;
            return (keyPrefix + (missingMods.Count == 1 ? "One" : "Many")).Translate(otherName, missingMods.Count);
        }

        /// <summary>
        /// What won't arrive if we accept now: animals and colonists' missing parts, and items from mods we don't have.
        /// </summary>
        /// <returns>The confirmation box's text, or null if everything will arrive</returns>
        private string describeLeftBehind()
        {
            List<string> parts = new List<string>();

            string missingParts = PawnCheck.DescribeAll(trade.OtherPartyItemsOnOffer);
            if (missingParts != null) parts.Add("Phinix_pawnCheck_partsLeftBehind".Translate(missingParts));

            string missingItems = ModCheck.Describe(theirMissingMods);
            if (missingItems != null) parts.Add("Phinix_modCheck_itemsLeftBehind".Translate(missingItems));

            if (parts.Count == 0) return null;
            return string.Join("\n\n", parts) + "\n\n" + "Phinix_trade_acceptAnyway".Translate();
        }

        /// <summary>
        /// Draws the clickable missing mods warning, with an arrow showing whether the list is open.
        /// </summary>
        /// <returns>Whether it was clicked</returns>
        private static bool drawModWarning(Rect rect, string text, bool open)
        {
            Widgets.DrawHighlightIfMouseover(rect);

            Rect arrowRect = new Rect(rect.x + 4f, rect.center.y - 6f, 12f, 12f);
            UnityEngine.GUI.DrawTexture(arrowRect, open ? TexButton.Collapse : TexButton.Reveal);

            SaveTextFormat();
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            UnityEngine.GUI.color = MISSING_PARTS_COLOR;
            Widgets.LabelFit(new Rect(arrowRect.xMax + 6f, rect.y, rect.xMax - arrowRect.xMax - 6f, rect.height), text);
            UnityEngine.GUI.color = Color.white;
            RestoreTextFormat();

            TooltipHandler.TipRegion(rect, "Phinix_modCheck_tip".Translate());
            return Widgets.ButtonInvisible(rect);
        }

        /// <summary>
        /// Draws the missing mods, each with a dropdown of the items in the offer that need it.
        /// </summary>
        private void drawMissingModList(Rect inRect, List<ModCheck.MissingMod> missingMods, ref Vector2 scrollPos, string side)
        {
            Widgets.DrawMenuSection(inRect);
            Rect outRect = inRect.ContractedBy(4f);

            // Measure first: a scroll view needs its total height before drawing
            float contentHeight = missingMods.Sum(mod => MOD_LIST_ROW_HEIGHT * (1 + (expandedMods.Contains(side + mod.PackageId) ? mod.Items.Count : 0)));
            float rowWidth = outRect.width - (contentHeight > outRect.height ? SCROLLBAR_WIDTH : 0f);
            Rect viewRect = new Rect(0f, 0f, rowWidth, contentHeight);

            SaveTextFormat();
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);
            float y = 0f;
            foreach (ModCheck.MissingMod mod in missingMods)
            {
                string key = side + mod.PackageId;
                bool expanded = expandedMods.Contains(key);

                // The mod, with how many items need it. Click to show or hide them
                Rect headerRect = new Rect(0f, y, rowWidth, MOD_LIST_ROW_HEIGHT);
                Widgets.DrawHighlightIfMouseover(headerRect);
                UnityEngine.GUI.DrawTexture(new Rect(headerRect.x + 4f, headerRect.center.y - 6f, 12f, 12f), expanded ? TexButton.Collapse : TexButton.Reveal);
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(headerRect.x + 22f, headerRect.y, headerRect.width - 22f, headerRect.height),
                    "Phinix_modCheck_modRow".Translate(mod.Name, mod.Items.Count).Truncate(headerRect.width - 22f));
                if (Widgets.ButtonInvisible(headerRect))
                {
                    if (expanded) expandedMods.Remove(key);
                    else expandedMods.Add(key);
                }
                y += MOD_LIST_ROW_HEIGHT;

                if (!expanded) continue;

                // Its items, indented and grey
                Text.Font = GameFont.Tiny;
                UnityEngine.GUI.color = Color.gray;
                foreach (string item in mod.Items)
                {
                    Rect itemRect = new Rect(30f, y, rowWidth - 30f, MOD_LIST_ROW_HEIGHT);
                    Widgets.Label(itemRect, item.Truncate(itemRect.width));
                    y += MOD_LIST_ROW_HEIGHT;
                }
                UnityEngine.GUI.color = Color.white;
            }
            Widgets.EndScrollView();
            RestoreTextFormat();
        }

        /// <summary>
        /// Draws a list of item stacks.
        /// </summary>
        /// <param name="inRect">Container to draw within</param>
        /// <param name="stacks">List of item stacks to draw</param>
        /// <param name="scrollPos">Scroll position</param>
        /// <param name="interactive">Whether to draw interactive buttons and quantity fields</param>
        private void drawItemStackList(Rect inRect, List<StackedThings> stacks, ref Vector2 scrollPos, bool interactive = false)
        {
            float ICON_WIDTH = 30f;
            float ROW_HEIGHT = ICON_WIDTH;
            float BUTTON_WIDTH = 40f;
            float QUANTITY_FIELD_WIDTH = 70f;
            float AVAILABLE_COUNT_WIDTH = 70f;
            float RIGHT_PADDING = 5f;

            // Set up the content rect and start scrolling
            bool scrollbarsPresent = ROW_HEIGHT * stacks.Count > inRect.height;
            int nonEmptyStacks = stacks.Count(stack => stack.Count != 0);
            Rect contentRect = new Rect(inRect.xMin, inRect.yMin, scrollbarsPresent ? inRect.width - SCROLLBAR_WIDTH : inRect.width, ROW_HEIGHT * nonEmptyStacks);
            bool scrollRequired = contentRect.height > inRect.height;
            if (scrollRequired) Widgets.BeginScrollView(inRect, ref scrollPos, contentRect);

            bool alternateBackground = false;
            float currentY = contentRect.yMin;
            foreach (StackedThings stack in stacks)
            {
                // Don't deal with empty stacks (Count is 1 for display-only animal rows, which have no Things)
                if (stack.Count == 0) continue;

                Rect rowRect = new Rect(contentRect.xMin, currentY, contentRect.width, ROW_HEIGHT);
                Rect iconRect = rowRect.LeftPartPixels(ICON_WIDTH);

                // Background
                if (alternateBackground) Widgets.DrawHighlight(rowRect);

                // Icon
                Widgets.ThingIcon(iconRect, stack.ThingDef, stack.StuffDef, stack.StyleDef, 0.9f);

                Rect itemNameRect;
                if (interactive)
                {
                    float buttonAreaWidth = ((BUTTON_WIDTH * 6) + QUANTITY_FIELD_WIDTH + AVAILABLE_COUNT_WIDTH + (DEFAULT_SPACING * 3));
                    Rect buttonAreaRect = new Rect(rowRect.xMax - (RIGHT_PADDING + buttonAreaWidth), rowRect.yMin, buttonAreaWidth, rowRect.height);
                    Rect quantityButton1Rect = new Rect(buttonAreaRect.xMin, buttonAreaRect.yMin, BUTTON_WIDTH, buttonAreaRect.height);
                    Rect quantityButton2Rect = quantityButton1Rect.TranslatedBy(BUTTON_WIDTH);
                    Rect quantityButton3Rect = quantityButton2Rect.TranslatedBy(BUTTON_WIDTH);
                    Rect quantityFieldRect = new Rect(quantityButton3Rect.xMax + DEFAULT_SPACING, buttonAreaRect.yMin, QUANTITY_FIELD_WIDTH, buttonAreaRect.height);
                    Rect availableCountRect = new Rect(quantityFieldRect.xMax + DEFAULT_SPACING, buttonAreaRect.yMin, AVAILABLE_COUNT_WIDTH, buttonAreaRect.height);
                    Rect quantityButton4Rect = new Rect(availableCountRect.xMax + DEFAULT_SPACING, buttonAreaRect.yMin, BUTTON_WIDTH, buttonAreaRect.height);
                    Rect quantityButton5Rect = quantityButton4Rect.TranslatedBy(BUTTON_WIDTH);
                    Rect quantityButton6Rect = quantityButton5Rect.TranslatedBy(BUTTON_WIDTH);

                    itemNameRect = new Rect(iconRect.xMax + DEFAULT_SPACING, rowRect.yMin, buttonAreaRect.xMin - iconRect.xMax - (DEFAULT_SPACING * 2), rowRect.height);

                    // -100 button
                    if (Widgets.ButtonText(quantityButton1Rect, "-100")) stack.Selected = Clamp(stack.Selected - 100, 0, stack.Count);

                    // -10 button
                    if (Widgets.ButtonText(quantityButton2Rect, "-10")) stack.Selected = Clamp(stack.Selected - 10, 0, stack.Count);

                    // -1 button
                    if (Widgets.ButtonText(quantityButton3Rect, "-1")) stack.Selected = Clamp(stack.Selected - 1, 0, stack.Count);

                    // +1 button
                    if (Widgets.ButtonText(quantityButton4Rect, "+1")) stack.Selected = Clamp(stack.Selected + 1, 0, stack.Count);

                    // +10 button
                    if (Widgets.ButtonText(quantityButton5Rect, "+10")) stack.Selected = Clamp(stack.Selected + 10, 0, stack.Count);

                    // +100 button
                    if (Widgets.ButtonText(quantityButton6Rect, "+100")) stack.Selected = Clamp(stack.Selected + 100, 0, stack.Count);

                    // Quantity text field
                    string buf = stack.Selected == 0 ? "" : stack.Selected.ToString();
                    buf = Widgets.TextField(quantityFieldRect, buf, 100, itemCountInputRegex);
                    stack.Selected = string.IsNullOrEmpty(buf) ? 0 : Clamp(int.Parse(buf), 0, stack.Count);

                    // Available count
                    SaveTextFormat();
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(availableCountRect, $"/ {stack.Count}");
                    RestoreTextFormat();
                }
                else
                {
                    Rect itemCountRect = new Rect(rowRect.xMax - QUANTITY_FIELD_WIDTH - RIGHT_PADDING, rowRect.yMin, QUANTITY_FIELD_WIDTH, rowRect.height);
                    itemNameRect = new Rect(iconRect.xMax + DEFAULT_SPACING, rowRect.yMin, itemCountRect.xMin - iconRect.xMax - DEFAULT_SPACING, rowRect.height);

                    // Item count
                    SaveTextFormat();
                    Text.Anchor = TextAnchor.MiddleRight;
                    Widgets.Label(itemCountRect, stack.Count.ToStringSI());
                    RestoreTextFormat();
                }

                // Pawns get an info button at the end of their name: offered pawns open the pawn inspector (there's no
                // real pawn behind them), our own open the game's normal info card
                Pawn ownPawn = stack.Things.FirstOrDefault() as Pawn;
                if (stack.DisplayPawn != null || ownPawn != null)
                {
                    Rect infoRect = new Rect(itemNameRect.xMax - INFO_BUTTON_SIZE, rowRect.center.y - INFO_BUTTON_SIZE / 2, INFO_BUTTON_SIZE, INFO_BUTTON_SIZE);
                    itemNameRect.width -= INFO_BUTTON_SIZE + 4f;

                    if (stack.DisplayPawn != null)
                    {
                        TooltipHandler.TipRegion(infoRect, "Phinix_pawn_inspectTip".Translate());
                        if (Widgets.ButtonImage(infoRect, TexButton.Info)) Find.WindowStack.Add(new PawnInspectWindow(stack.DisplayPawn));
                    }
                    else
                    {
                        Widgets.InfoCardButton(infoRect.x, infoRect.y, ownPawn);
                    }
                }

                // Item name. An offered pawn with parts our game doesn't have is shown in orange, with what won't come
                // in its tooltip
                string missingParts = stack.DisplayPawn != null ? PawnCheck.Describe(stack.DisplayPawn) : null;
                SaveTextFormat();
                Text.Anchor = TextAnchor.MiddleLeft;
                if (missingParts != null)
                {
                    UnityEngine.GUI.color = MISSING_PARTS_COLOR;
                    TooltipHandler.TipRegion(itemNameRect, "Phinix_pawnCheck_rowTip".Translate(missingParts));
                }
                Widgets.LabelFit(itemNameRect, stack.Label);
                UnityEngine.GUI.color = Color.white;
                RestoreTextFormat();

                // Toggle alternate background colour
                alternateBackground = !alternateBackground;

                currentY += ROW_HEIGHT;
            }

            if (scrollRequired) Widgets.EndScrollView();
        }
    }
}
