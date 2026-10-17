# Contributing

Issues and pull requests are welcome.

- One change per pull request, with the tests that prove it. `tools/check.sh`
  runs everything a reviewer will run.
- Match the surrounding code. The scheduling core (`scheduler/src/Luftpause.Scheduling`)
  stays pure functions over immutable state; I/O lives in the hosts.
- Changes to admission or routing come with numbers from `tools/bench`, since
  the README's claims are measured, not asserted.
- Sign off your commits (`git commit -s`) to certify the
  [Developer Certificate of Origin](https://developercertificate.org/).

## License of contributions

luftpause is distributed under the [Elastic License 2.0](LICENSE). By submitting
a contribution you agree that it is licensed under the same terms, and you grant
the project the right to also distribute it under any OSI-approved open source
license the project adopts later. No other paperwork is required.
