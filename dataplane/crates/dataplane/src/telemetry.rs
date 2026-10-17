use std::env;

use axum::http::{HeaderMap, HeaderName, HeaderValue};
use opentelemetry::Context;
use opentelemetry::propagation::{Extractor, Injector, TextMapPropagator};
use opentelemetry::trace::{TraceContextExt, TracerProvider};
use opentelemetry_sdk::propagation::TraceContextPropagator;
use opentelemetry_sdk::trace::SdkTracerProvider;
use tracing_subscriber::layer::SubscriberExt;

pub fn init() -> SdkTracerProvider {
    let builder = SdkTracerProvider::builder();
    let builder = match env::var("OTEL_EXPORTER_OTLP_ENDPOINT") {
        Ok(endpoint) if !endpoint.is_empty() => builder.with_batch_exporter(
            opentelemetry_otlp::SpanExporter::builder().with_http().build().expect("OTLP span exporter"),
        ),
        _ => builder,
    };
    let provider = builder.build();
    let layer = tracing_opentelemetry::layer().with_tracer(provider.tracer("dataplane"));
    tracing::subscriber::set_global_default(tracing_subscriber::registry().with(layer)).expect("tracing subscriber");
    provider
}

pub fn parent_context(headers: &HeaderMap) -> Context {
    TraceContextPropagator::new().extract(&Headers(headers))
}

pub fn trace_headers(cx: &Context) -> HeaderMap {
    let mut headers = HeaderMap::new();
    TraceContextPropagator::new().inject_context(cx, &mut HeadersMut(&mut headers));
    headers
}

pub fn trace_ids(cx: &Context) -> (String, String) {
    let span = cx.span();
    let span = span.span_context();
    if span.is_valid() {
        (span.trace_id().to_string(), span.span_id().to_string())
    } else {
        (String::new(), String::new())
    }
}

struct Headers<'a>(&'a HeaderMap);

impl Extractor for Headers<'_> {
    fn get(&self, key: &str) -> Option<&str> {
        self.0.get(key).and_then(|v| v.to_str().ok())
    }

    fn keys(&self) -> Vec<&str> {
        self.0.keys().map(HeaderName::as_str).collect()
    }
}

struct HeadersMut<'a>(&'a mut HeaderMap);

impl Injector for HeadersMut<'_> {
    fn set(&mut self, key: &str, value: String) {
        let name = HeaderName::from_bytes(key.as_bytes()).expect("propagator header name");
        self.0.insert(name, HeaderValue::from_str(&value).expect("propagator header value"));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const TRACEPARENT: &str = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    fn incoming(value: &str) -> HeaderMap {
        let mut headers = HeaderMap::new();
        headers.insert("traceparent", HeaderValue::from_str(value).unwrap());
        headers
    }

    #[test]
    fn an_incoming_traceparent_becomes_the_parent_context() {
        let cx = parent_context(&incoming(TRACEPARENT));

        assert_eq!(
            trace_ids(&cx),
            ("4bf92f3577b34da6a3ce929d0e0e4736".to_owned(), "00f067aa0ba902b7".to_owned())
        );
    }

    #[test]
    fn a_context_is_forwarded_as_the_same_traceparent() {
        let headers = trace_headers(&parent_context(&incoming(TRACEPARENT)));

        assert_eq!(headers.get("traceparent").unwrap(), TRACEPARENT);
    }

    #[test]
    fn without_a_traceparent_there_are_no_ids_and_nothing_to_forward() {
        let cx = parent_context(&HeaderMap::new());

        assert_eq!(trace_ids(&cx), (String::new(), String::new()));
        assert!(trace_headers(&cx).is_empty());
    }

    #[test]
    fn a_malformed_traceparent_is_ignored() {
        assert_eq!(trace_ids(&parent_context(&incoming("00-nonsense-01"))), (String::new(), String::new()));
    }
}
