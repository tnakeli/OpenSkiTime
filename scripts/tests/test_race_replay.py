"""Tests for the real-time race replay tool that need no waiting: schedule, line format and CSV validation."""
import contextlib
import importlib.util
import io
from pathlib import Path
import re
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('race_replay', Path(__file__).resolve().parents[1] / 'race-replay.py')
race_replay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(race_replay)

UNITS = race_replay.UNITS
# The impulse pattern of OpenSkiTime's ALGE ASCII decoder (AlgeDecoders.ImpulsePattern).
IMPULSE = re.compile(r'^\s*(?P<flag>[?mMcCdDinxXtT*])?\s*(?P<number>\d{1,5})(?P<star>\*)?\s+[cC](?P<channel>[0-8])(?P<manual>[mM])?\s+'
                     r'(?P<time>\d{2}:\d{2}:\d{2}[.:]\d{1,7})(?:\s+\d{2}(?:\s*\d{1,4})?)?\s*$')


def write_csv(folder, name, text):
    path = Path(folder) / name
    path.write_text(text, encoding='utf-8')
    return path


class TimeTests(unittest.TestCase):
    def test_parses_whole_hundredths(self):
        self.assertEqual(race_replay.parse_time('52.40'), 524_000)
        self.assertEqual(race_replay.parse_time('1:02.34'), 623_400)

    def test_rejects_other_precision_and_invalid_seconds(self):
        for text in ('52.4', '52.401', '1:60.00', 'abc', '52'):
            with self.assertRaises(ValueError):
                race_replay.parse_time(text)


class ScheduleTests(unittest.TestCase):
    def test_finish_minus_start_truncates_to_the_given_time_and_dns_has_no_impulse(self):
        run = [(1, 'FINISHED', 524_000, []), (2, 'DNF', None, []), (3, 'DNS', None, []), (4, 'DSQ', 530_100, [])]
        events = race_replay.schedule([run], 10 * 3600 * UNITS, 30 * UNITS, 120 * UNITS)
        by = {(bib, channel): at for at, _, bib, channel in events}
        self.assertEqual(sorted(by), [(1, 0), (1, 1), (2, -1), (2, 0), (4, 0), (4, 1)])
        self.assertEqual(by[(2, -1)] - by[(2, 0)], 8 * UNITS)  # DNF without a timing point: 8 s after the start
        for bib, given in ((1, 524_000), (4, 530_100)):
            elapsed = by[(bib, 1)] - by[(bib, 0)]
            self.assertEqual(elapsed // 100 * 100, given)  # difference truncated to hundredths
            self.assertEqual(by[(bib, 1)] // 100 * 100 - by[(bib, 0)] // 100 * 100, given)  # each truncated first
        self.assertEqual([at for at, *_ in events], sorted(at for at, *_ in events))

    def test_intermediates_truncate_to_the_given_times_and_a_dnf_follows_the_last_timing_point(self):
        run = [(1, 'FINISHED', 524_000, [163_000, 408_800]), (2, 'DNF', None, [169_800, None])]
        events = race_replay.schedule([run], 10 * 3600 * UNITS, 30 * UNITS, 0, inter_channels=(2, 3))
        by = {(bib, channel): at for at, _, bib, channel in events}
        self.assertEqual(sorted(by), [(1, 0), (1, 1), (1, 2), (1, 3), (2, -1), (2, 0), (2, 2)])
        for channel, given in ((2, 163_000), (3, 408_800), (1, 524_000)):
            self.assertEqual((by[(1, channel)] - by[(1, 0)]) // 100 * 100, given)
        self.assertEqual((by[(2, 2)] - by[(2, 0)]) // 100 * 100, 169_800)
        self.assertEqual(by[(2, -1)] - by[(2, 2)], 5 * UNITS)

    def test_starts_follow_start_order_at_the_interval(self):
        run = [(9, 'FINISHED', 524_000, []), (2, 'FINISHED', 524_000, []), (5, 'FINISHED', 524_000, [])]
        events = race_replay.schedule([run], 0, 30 * UNITS, 0)
        starts = [(bib, at // UNITS) for at, _, bib, channel in events if channel == 0]
        self.assertEqual(starts, [(9, 0), (2, 30), (5, 60)])

    def test_next_run_starts_a_full_break_after_the_last_impulse_of_the_previous_run(self):
        run1 = [(1, 'FINISHED', 524_000, []), (2, 'FINISHED', 600_000, [])]
        run2 = [(2, 'FINISHED', 524_000, []), (1, 'DNF', None, [])]
        events = race_replay.schedule([run1, run2], 0, 30 * UNITS, 120 * UNITS)
        last1 = max(at for at, run, *_ in events if run == 1)
        self.assertIn((last1 + 10 * UNITS, 2, 0, -2), events)  # next run prepared 10 s after Run 1
        events = [x for x in events if x[3] != -2]
        first2 = min(at for at, run, *_ in events if run == 2)
        self.assertGreaterEqual(first2 - last1, 120 * UNITS)
        self.assertLess(first2 - last1, 121 * UNITS + 100 * 100)

    def test_refuses_a_replay_past_midnight(self):
        with self.assertRaises(SystemExit):
            race_replay.schedule([[(1, 'FINISHED', 524_000, [])]], 23 * 3600 * UNITS + 59 * 60 * UNITS + 30 * UNITS, UNITS, 0)


class LineTests(unittest.TestCase):
    def test_line_is_an_explicit_bib_impulse_the_decoder_reads(self):
        text = race_replay.line(10 * 3600 * UNITS + 52 * UNITS + 4037, 7, 1)
        self.assertEqual(text, ' 0007* C1 10:00:52.4037\r')
        match = IMPULSE.match(text[:-1])
        self.assertEqual((match['number'], match['star'], match['channel'], match['time']), ('0007', '*', '1', '10:00:52.4037'))


class CsvTests(unittest.TestCase):
    def test_reads_rows_in_start_order(self):
        with tempfile.TemporaryDirectory() as folder:
            path = write_csv(folder, 'run.csv', 'Bib,Code,Status,Time,Inter1\n4,,Finished,52.40,16.30\n,5501,DNF,,\n7,5502,,1:01.00,\n')
            self.assertEqual(race_replay.read_run(path),
                             [(4, None, 'FINISHED', 524_000, [163_000]), (None, '5501', 'DNF', None, [None]), (7, '5502', 'FINISHED', 610_000, [None])])

    def test_rejects_repeated_racers_unknown_status_missing_times_and_unnamed_racers(self):
        for text in ('Bib,Status,Time\n4,Finished,52.40\n4,Finished,53.00\n', 'Code,Status,Time\n9,DNF,\n9,DNF,\n',
                     'Bib,Status,Time\n4,NPS,\n', 'Bib,Status,Time\n4,Finished,\n', 'Bib,Status,Time\n,DNF,\n'):
            with tempfile.TemporaryDirectory() as folder, contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(SystemExit):
                    race_replay.read_run(write_csv(folder, 'run.csv', text))

    def test_intermediate_columns_keep_their_numbers(self):
        with tempfile.TemporaryDirectory() as folder:
            path = write_csv(folder, 'run.csv', 'Bib,Status,Time,Inter2\n4,Finished,52.40,40.88\n')
            self.assertEqual(race_replay.read_run(path), [(4, None, 'FINISHED', 524_000, [None, 408_800])])
            with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit):
                race_replay.read_run(write_csv(folder, 'zero.csv', 'Bib,Status,Time,Inter0\n4,Finished,52.40,16.30\n'))

    def test_start_list_supplies_the_apps_bibs_for_every_run_and_its_order_for_run_1(self):
        with tempfile.TemporaryDirectory() as folder:
            start_list = race_replay.read_start_list(write_csv(folder, 'list.tsv',
                'Position\tBib\tCode\tSurname\tFirst name\tYear\tNation\tFIS points\n'
                '1\t11\t5502\tB\tB\t2000\tFIN\t1.00\n2\t12\t5501\tA\tA\t2000\tFIN\t2.00\n3\t13\t5503\tC\tC\t2000\tFIN\t3.00\n'))
            run1 = [(1, '5501', 'FINISHED', 524_000, []), (2, '5502', 'FINISHED', 525_000, []), (3, '5503', 'DNF', None, [])]
            run2 = [(2, '5502', 'FINISHED', 530_000, []), (1, '5501', 'FINISHED', 531_000, [])]
            self.assertEqual(race_replay.resolve([run1, run2], start_list),
                             [[(11, 'FINISHED', 525_000, []), (12, 'FINISHED', 524_000, []), (13, 'DNF', None, [])],
                              [(11, 'FINISHED', 530_000, []), (12, 'FINISHED', 531_000, [])]])
            # Replaying Run 2 alone keeps its CSV order.
            self.assertEqual(race_replay.resolve([run2], start_list, first_run=2), [[(11, 'FINISHED', 530_000, []), (12, 'FINISHED', 531_000, [])]])
            with contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(SystemExit):  # Run 1 misses a racer of the start list
                    race_replay.resolve([run1[:2]], start_list)
                with self.assertRaises(SystemExit):  # a racer the app has not drawn
                    race_replay.resolve([run1 + [(4, '5504', 'DNF', None, [])]], start_list)
                with self.assertRaises(SystemExit):  # codes without a start list
                    race_replay.resolve([[(None, '5501', 'DNF', None, [])]])

    def test_dry_run_prints_without_writing(self):
        with tempfile.TemporaryDirectory() as folder:
            run = write_csv(folder, 'run.csv', 'Bib,Status,Time\n1,Finished,52.40\n')
            out = Path(folder) / 'replay.txt'
            printed = io.StringIO()
            with contextlib.redirect_stdout(printed):
                race_replay.main(['--out', str(out), '--run', str(run), '--first-start', '10:00:00', '--dry-run'])
            self.assertFalse(out.exists())
            self.assertIn('run 1 0001* C0 10:00:00.0700', printed.getvalue())
            self.assertIn('run 1 0001* C1 10:00:52.4748', printed.getvalue())


if __name__ == '__main__':
    unittest.main()
