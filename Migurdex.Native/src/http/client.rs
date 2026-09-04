use dashmap::DashMap;
use std::sync::LazyLock;
use wreq::Client;
use wreq_util::Profile;

#[derive(Hash, Eq, PartialEq, Clone, Copy, Debug)]
pub struct ClientKey {
    pub emulation: Profile,
    pub skip_cert_verify: bool,
    pub no_redirect: bool,
}

static CLIENTS: LazyLock<DashMap<ClientKey, Client>> = LazyLock::new(DashMap::new);

pub fn get_client_with_options(key: ClientKey) -> Client {
    CLIENTS
        .entry(key)
        .or_insert_with(|| {
            let mut builder = Client::builder()
                .emulation(key.emulation)
                .cookie_store(true);

            if key.skip_cert_verify {
                builder = builder.tls_cert_verification(false);
            }

            if key.no_redirect {
                builder = builder.redirect(wreq::redirect::Policy::none());
            }

            builder.build().expect("Failed to create client")
        })
        .clone()
}

pub fn get_client(emulation: Profile) -> Client {
    get_client_with_options(ClientKey {
        emulation,
        skip_cert_verify: false,
        no_redirect: false,
    })
}

pub fn get_no_redirect_client(emulation: Profile) -> Client {
    get_client_with_options(ClientKey {
        emulation,
        skip_cert_verify: false,
        no_redirect: true,
    })
}
