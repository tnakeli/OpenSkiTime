// OpenSkiTime Live API reader for Java 17 or later. MIT license. https://openskiti.me/live-api/
//
// Needs com.microsoft.signalr:signalr:10.0.15 and its dependencies on the class path, for example with JBang:
//   jbang LiveReader.java                                        lists the races published now
//   jbang LiveReader.java https://live.openskiti.me <sessionId>  follows one race
//JAVA 17+
//DEPS com.microsoft.signalr:signalr:10.0.15
import com.google.gson.Gson;
import com.microsoft.signalr.HubConnection;
import com.microsoft.signalr.HubConnectionBuilder;
import com.microsoft.signalr.HubConnectionState;
import java.io.PrintStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.TimeUnit;
import java.util.function.Supplier;

public final class LiveReader {
    // The messages. Properties not listed here are ignored; add the ones you need.
    static final class Race { String sessionId, name, discipline, date; }
    static final class Competition { String name, discipline, date, codex; }
    static final class Competitor { int bib; String lastName, firstName, nation, club, fisCode, category; }
    static final class Intermediate { int number; long hundredths; }
    static final class Forerunner { String letter, lastName, firstName, nation, status; }
    static final class Row {
        int bib; String status, at, startedAt;
        Long hundredths, difference, totalHundredths, totalDifference; Integer rank, totalRank;
        Intermediate[] intermediates;
    }
    static final class Run { int number; int[] startOrder; Row[] results; Forerunner[] forerunners; }
    static final class State {
        long version; Competition competition; Competitor[] competitors; int currentRun; Run[] runs; String updatedAt; boolean paused;
    }
    static final class SyncMessage { String status, generation; long revision; State state; }
    static final class RunDelta { int number; Row[] results; int[] order; Forerunner[] forerunners; }
    static final class DeltaMessage {
        String generation, updatedAt; long baseRevision, revision, version; boolean paused; RunDelta[] runs;
    }
    static final class BeaconMessage { String status, generation; long revision; }

    enum Phase { SUBSCRIBING, LIVE, AWAITING, DELETED }
    enum Outcome { NONE, CHANGED, RESYNC }
    enum Kind { STATUS, TIME, INTERMEDIATE }
    record Change(int run, Row row, Kind kind, int number, Long hundredths) { }

    interface Listener {
        void state(State state, List<Change> changes);
        void status(Phase phase);
    }

    /** The state of one race, kept up to date from the messages of the server. */
    static final class RaceState {
        Phase phase = Phase.SUBSCRIBING;
        String generation;
        long revision;
        State state;
        long resyncSince; // When the complete state was asked for (System.nanoTime), or 0.

        /** A new connection: the previous state stays until the subscription is answered. */
        void connected() { phase = Phase.SUBSCRIBING; generation = null; revision = 0; resyncSince = 0; }

        /** The complete state, or the reason why there is none. Always the newest on this connection. */
        Outcome sync(SyncMessage message) {
            boolean live = "Live".equals(message.status);
            resyncSince = 0;
            phase = live ? Phase.LIVE : "Deleted".equals(message.status) ? Phase.DELETED : Phase.AWAITING;
            generation = live ? message.generation : null;
            revision = live ? message.revision : 0;
            if (live) { state = message.state; } else if (phase == Phase.DELETED) { state = null; }
            return Outcome.CHANGED;
        }

        /** One change. It applies only to the revision it names. */
        Outcome delta(DeltaMessage message) {
            if (phase == Phase.SUBSCRIBING || resyncSince != 0) { return Outcome.NONE; }
            if (phase != Phase.LIVE || !message.generation.equals(generation)) { return resync(); }
            if (message.revision <= revision) { return Outcome.NONE; }
            if (message.baseRevision != revision) { return resync(); }
            State next = merge(state, message);
            if (next == null) { return resync(); }
            revision = message.revision;
            state = next;
            return Outcome.CHANGED;
        }

        /** What the server has now: confirms the state, or shows that a message was missed. */
        Outcome beacon(BeaconMessage message) {
            if (phase == Phase.SUBSCRIBING) { return Outcome.NONE; }
            if (resyncSince != 0) { return System.nanoTime() - resyncSince >= TimeUnit.SECONDS.toNanos(30) ? resync() : Outcome.NONE; }
            boolean agrees = "Live".equals(message.status)
                ? phase == Phase.LIVE && Objects.equals(message.generation, generation) && message.revision == revision
                : phase == ("Deleted".equals(message.status) ? Phase.DELETED : Phase.AWAITING);
            return agrees ? Outcome.NONE : resync();
        }

        private Outcome resync() { resyncSince = System.nanoTime(); return Outcome.RESYNC; }

        // The state after a delta, or null when the delta does not fit it.
        private static State merge(State state, DeltaMessage delta) {
            Map<Integer, RunDelta> changed = new LinkedHashMap<>();
            for (RunDelta run : delta.runs) { changed.put(run.number, run); }
            List<Run> runs = new ArrayList<>();
            for (Run run : state.runs) {
                RunDelta change = changed.remove(run.number);
                if (change == null) { runs.add(run); continue; }
                Map<Integer, Row> rows = rows(run); // Keeps known rows in place and appends new ones.
                for (Row row : change.results) { rows.put(row.bib, row); }
                int[] order = change.order != null ? change.order : rows.keySet().stream().mapToInt(x -> x).toArray();
                if (order.length != rows.size() || !new HashSet<>(Arrays.stream(order).boxed().toList()).equals(rows.keySet())) { return null; }
                Run next = new Run();
                next.number = run.number;
                next.startOrder = run.startOrder;
                next.results = Arrays.stream(order).mapToObj(rows::get).toArray(Row[]::new);
                next.forerunners = change.forerunners != null ? change.forerunners : run.forerunners;
                runs.add(next);
            }
            if (!changed.isEmpty()) { return null; }
            State next = new State();
            next.competition = state.competition;
            next.competitors = state.competitors;
            next.currentRun = state.currentRun;
            next.version = delta.version;
            next.updatedAt = delta.updatedAt;
            next.paused = delta.paused;
            next.runs = runs.toArray(Run[]::new);
            return next;
        }
    }

    private static Map<Integer, Row> rows(Run run) {
        Map<Integer, Row> rows = new LinkedHashMap<>();
        if (run != null) { for (Row row : run.results) { rows.put(row.bib, row); } }
        return rows;
    }

    private static Map<Integer, Long> times(Row row) {
        Map<Integer, Long> times = new TreeMap<>();
        if (row != null && row.intermediates != null) { for (Intermediate x : row.intermediates) { times.put(x.number, x.hundredths); } }
        return times;
    }

    /** What happened between two states: status changes, intermediate times and corrected run times. */
    static List<Change> changes(State before, State after) {
        List<Change> result = new ArrayList<>();
        for (Run run : after.runs) {
            Map<Integer, Row> rows = rows(Arrays.stream(before.runs).filter(x -> x.number == run.number).findFirst().orElse(null));
            for (Row row : run.results) {
                Row old = rows.get(row.bib);
                List<Change> status = new ArrayList<>(), intermediates = new ArrayList<>();
                if (!row.status.equals(old == null ? "Ready" : old.status)) { status.add(new Change(run.number, row, Kind.STATUS, 0, null)); }
                else if ("Finished".equals(row.status) && !Objects.equals(row.hundredths, old == null ? null : old.hundredths)) {
                    status.add(new Change(run.number, row, Kind.TIME, 0, null));
                }
                Map<Integer, Long> times = times(row), oldTimes = times(old);
                TreeSet<Integer> numbers = new TreeSet<>(times.keySet());
                numbers.addAll(oldTimes.keySet());
                for (int number : numbers) {
                    if (!Objects.equals(times.get(number), oldTimes.get(number))) {
                        intermediates.add(new Change(run.number, row, Kind.INTERMEDIATE, number, times.get(number)));
                    }
                }
                // When a break in the connection joined several changes, a start comes before and a finish after the intermediates.
                boolean started = "OnCourse".equals(row.status);
                result.addAll(started ? status : intermediates);
                result.addAll(started ? intermediates : status);
            }
        }
        return result;
    }

    /** Follows one race until it is deleted. The callbacks run one at a time. */
    static void follow(String server, String sessionId, Listener listener) throws InterruptedException {
        RaceState race = new RaceState();
        ScheduledExecutorService timer = Executors.newSingleThreadScheduledExecutor();
        try {
            for (int attempt = 0; ;) {
                Connection connection = new Connection(server, race, timer, listener);
                try {
                    connection.run(sessionId);
                    attempt = 0;
                } catch (RuntimeException error) {
                    if (connection.hub.getConnectionState() == HubConnectionState.CONNECTED) { throw error; } // The server refused the subscription.
                } finally {
                    connection.stop();
                }
                synchronized (race) { if (race.phase == Phase.DELETED) { return; } }
                long delay = Math.min(15000, 1000L << Math.min(attempt++, 4));
                Thread.sleep((long) (delay * (0.5 + ThreadLocalRandom.current().nextDouble() / 2)));
            }
        } finally {
            timer.shutdownNow();
        }
    }

    private static final class Connection {
        final HubConnection hub;
        private final RaceState race;
        private final ScheduledExecutorService timer;
        private final Listener listener;
        private final CompletableFuture<Void> closed = new CompletableFuture<>();
        private long lastResync;

        Connection(String server, RaceState race, ScheduledExecutorService timer, Listener listener) {
            this.race = race;
            this.timer = timer;
            this.listener = listener;
            hub = HubConnectionBuilder.create(server + "/live").build();
            hub.onClosed(error -> closed.complete(null));
            hub.on("Sync", message -> handle(() -> race.sync(message)), SyncMessage.class);
            hub.on("Delta", message -> handle(() -> race.delta(message)), DeltaMessage.class);
            hub.on("Beacon", message -> handle(() -> race.beacon(message)), BeaconMessage.class);
            synchronized (race) { race.connected(); }
        }

        void run(String sessionId) {
            hub.start().blockingAwait();
            hub.invoke("WatchSync", sessionId, 1).blockingAwait();
            closed.join();
        }

        void stop() {
            try { hub.stop().blockingAwait(5, TimeUnit.SECONDS); } catch (RuntimeException error) { /* Already closed. */ }
        }

        private void handle(Supplier<Outcome> step) {
            synchronized (race) {
                State before = race.state;
                Outcome outcome = step.get();
                if (outcome == Outcome.RESYNC) {
                    long earliest = lastResync == 0 ? 0 : lastResync + 3000 - System.currentTimeMillis();
                    timer.schedule(this::resync, Math.max(ThreadLocalRandom.current().nextLong(500, 5000), earliest), TimeUnit.MILLISECONDS);
                }
                if (outcome != Outcome.CHANGED) { return; }
                if (race.phase == Phase.LIVE) { listener.state(race.state, before == null ? List.of() : changes(before, race.state)); }
                else { listener.status(race.phase); }
                if (race.phase == Phase.DELETED) { closed.complete(null); }
            }
        }

        private void resync() {
            synchronized (race) {
                if (race.resyncSince == 0 || hub.getConnectionState() != HubConnectionState.CONNECTED) { return; }
                lastResync = System.currentTimeMillis();
            }
            try { hub.send("Resync"); } catch (RuntimeException error) { /* The connection broke; the loop connects again. */ }
        }
    }

    /** Hundredths as 50.12 or 1:02.03, with integer arithmetic. */
    static String time(long x) {
        return x >= 6000 ? String.format(Locale.ROOT, "%d:%02d.%02d", x / 6000, x / 100 % 60, x % 100) : String.format(Locale.ROOT, "%d.%02d", x / 100, x % 100);
    }

    private static String describe(Change change) {
        Row row = change.row();
        if (change.kind() == Kind.INTERMEDIATE) {
            return "Intermediate " + change.number() + " " + (change.hundredths() == null ? "removed" : time(change.hundredths()));
        }
        if (!"Finished".equals(row.status)) { return row.status; }
        return (change.kind() == Kind.TIME ? "Time changed " : "Finished ") + time(row.hundredths) + ", rank " + row.rank;
    }

    public static void main(String[] args) throws Exception {
        PrintStream out = new PrintStream(System.out, true, StandardCharsets.UTF_8);
        String server = args.length > 0 ? args[0] : "https://live.openskiti.me";
        if (args.length < 2) {
            // The hosted service sleeps between races: the first answer can take a couple of minutes.
            HttpRequest request = HttpRequest.newBuilder(URI.create(server + "/api/sessions")).timeout(Duration.ofMinutes(2)).build();
            HttpResponse<String> response = HttpClient.newHttpClient().send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() != 200) { throw new IllegalStateException("HTTP " + response.statusCode()); }
            Race[] races = new Gson().fromJson(response.body(), Race[].class);
            if (races.length == 0) { out.println("No race is published now."); }
            for (Race race : races) { out.println(race.sessionId + " | " + race.date + " | " + race.discipline + " | " + race.name); }
            return;
        }
        follow(server, args[1], new Listener() {
            private boolean named;

            @Override public void state(State state, List<Change> changes) {
                if (!named) { out.println("Race | " + state.competition.name + " | " + state.competitors.length + " competitors"); }
                named = true;
                for (Change change : changes) {
                    Competitor who = Arrays.stream(state.competitors).filter(x -> x.bib == change.row().bib).findFirst().orElse(null);
                    String name = who == null ? " " : who.lastName + " " + who.firstName;
                    out.println("Run " + change.run() + " | " + change.row().bib + " " + name + " | " + describe(change));
                }
            }

            @Override public void status(Phase phase) { out.println(phase == Phase.DELETED ? "Deleted" : "Waiting for the publisher"); }
        });
        System.exit(0); // The HTTP client of the SignalR library keeps threads alive for a while.
    }
}
